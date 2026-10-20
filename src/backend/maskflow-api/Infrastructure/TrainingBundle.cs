using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

// The downloadable package of a training run: everything needed to reproduce it and use its model.
//   README.md, train.py, predict.py, requirements.txt, training.json
//   base/      the base weight the run started from
//   dataset/   the exact dataset it trained on (with split-report.json)
//   runs/      every file the training produced (weights, ONNX, plots, predictions, csv, args, logs)
public sealed partial class MaskFlowStore
{
    static readonly string[] StoredExtensions = [".jpg", ".jpeg", ".png", ".pt", ".onnx", ".zip"];

    public async Task<string> BuildTrainingBundleAsync(TrainingRun run, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"maskflow-bundle-{run.Id}-{Util.Id()}.zip");
        try
        {
            var environment = await ReadJsonArtifactAsync(run, "environment.json", cancellationToken);
            var project = State.Projects.FirstOrDefault(x => x.Id == run.ProjectId);
            var baseWeight = run.Artifacts.FirstOrDefault(x => x.Name.StartsWith("base/", StringComparison.Ordinal));
            var dataset = run.Artifacts.FirstOrDefault(x => x.Name == DatasetArtifact);
            var text = new TrainingBundleText(run, project?.Name, environment, baseWeight is not null, dataset is not null);

            await using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                await AddTextAsync(zip, "README.md", text.Readme());
                await AddTextAsync(zip, "train.py", text.TrainScript());
                await AddTextAsync(zip, "predict.py", text.PredictScript());
                await AddTextAsync(zip, "requirements.txt", text.Requirements());
                await AddTextAsync(zip, "training.json", text.Metadata());

                foreach (var artifact in run.Artifacts)
                {
                    if (artifact.Name == DatasetArtifact)
                    {
                        await AddDatasetAsync(zip, artifact, cancellationToken);
                        continue;
                    }

                    var target = artifact.Name.StartsWith("base/", StringComparison.Ordinal) ? artifact.Name : $"runs/{artifact.Name}";
                    await using var source = await OpenStoredObjectAsync(artifact.Path);
                    await using var entry = zip.CreateEntry(target, CompressionFor(target)).Open();
                    await source.CopyToAsync(entry, cancellationToken);
                }
            }

            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    async Task AddDatasetAsync(ZipArchive zip, TrainingArtifact artifact, CancellationToken cancellationToken)
    {
        // Reading a zip needs a seekable stream; object storage streams are not, so stage it locally.
        var local = Path.Combine(Path.GetTempPath(), $"maskflow-dataset-{Util.Id()}.zip");
        try
        {
            await using (var source = await OpenStoredObjectAsync(artifact.Path))
            await using (var copy = File.Create(local))
            {
                await source.CopyToAsync(copy, cancellationToken);
            }

            using var dataset = ZipFile.OpenRead(local);
            foreach (var entry in dataset.Entries)
            {
                if (entry.FullName.EndsWith('/') || !IsSafeArtifactPath(entry.FullName)) continue;
                var target = $"dataset/{entry.FullName}";
                await using var input = entry.Open();
                await using var output = zip.CreateEntry(target, CompressionFor(target)).Open();
                await input.CopyToAsync(output, cancellationToken);
            }
        }
        finally
        {
            File.Delete(local);
        }
    }

    async Task<JsonElement?> ReadJsonArtifactAsync(TrainingRun run, string name, CancellationToken cancellationToken)
    {
        var artifact = FindArtifact(run, name);
        if (artifact is null) return null;
        try
        {
            using var document = JsonDocument.Parse(await ReadStoredBytesAsync(artifact.Path, cancellationToken));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    static async Task AddTextAsync(ZipArchive zip, string name, string content)
    {
        await using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        await writer.WriteAsync(content.Replace("\r\n", "\n"));
    }

    // Images, weights and archives are already compressed.
    static CompressionLevel CompressionFor(string name) =>
        StoredExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
}

public sealed class TrainingBundleText(TrainingRun run, string? projectName, JsonElement? environment, bool hasBase, bool hasDataset)
{
    readonly string baseWeight = run.Request.BaseWeight;
    readonly bool classify = run.Format == "classification-crops";
    readonly bool hasTest = run.Metrics is { ValueKind: JsonValueKind.Object } metrics && metrics.TryGetProperty("test", out _);
    string Env(string key) => environment is { } env && env.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    string TaskLabel => run.Task switch { "segment" => "实例分割", "classify" => "图像分类", _ => "目标检测" };
    string Split => run.Request.Split is { } split ? $"{split.Train}/{split.Val}/{split.Test}" : "70/20/10";

    public string Readme()
    {
        var text = new StringBuilder();
        text.AppendLine($"# {run.Name} — MaskFlow 训练包").AppendLine();
        text.AppendLine($"- 项目：{projectName ?? run.ProjectId}");
        text.AppendLine($"- 训练 ID：`{run.Id}`，状态：{run.Status}，完成 {run.Epoch}/{run.Request.Epochs} 轮");
        text.AppendLine($"- 任务：{TaskLabel}，基础权重：`{baseWeight}`" + (Env("baseWeightSha256") is { Length: > 0 } sha ? $"（SHA-256 `{sha}`）" : ""));
        text.AppendLine($"- 参数：图片尺寸 {run.Request.ImageSize}，批大小 {run.Request.Batch}，早停 {run.Request.Patience} 轮，划分 训练/验证/测试 = {Split}（随机种子 {run.Request.Seed}），{(run.Request.ConfirmedOnly ? "只用人工确认的标注" : "包含未确认的 AI 标注")}");
        text.AppendLine($"- 参与训练的图片：{run.ImageCount} 张");
        text.AppendLine($"- 时间：{run.StartedAt?.ToLocalTime():yyyy-MM-dd HH:mm} – {run.FinishedAt?.ToLocalTime():yyyy-MM-dd HH:mm}");
        var stack = new[] { ("Ultralytics", "ultralytics"), ("PyTorch", "torch"), ("CUDA", "cuda"), ("GPU", "gpu"), ("Python", "python") }
            .Where(x => Env(x.Item2).Length > 0).Select(x => $"{x.Item1} {Env(x.Item2)}").ToList();
        if (stack.Count > 0) text.AppendLine($"- 环境：{string.Join("，", stack)}");

        if (run.Error is not null) text.AppendLine($"- 说明：{run.Error}");
        text.AppendLine();
        AppendMetrics(text);

        text.AppendLine("## 目录").AppendLine();
        text.AppendLine("| 路径 | 内容 |").AppendLine("| --- | --- |");
        text.AppendLine(hasBase ? $"| `base/{baseWeight}` | 训练起点的基础权重（官方预训练模型） |" : $"| `base/` | 未保存（旧版本训练）；可从 https://github.com/ultralytics/assets/releases 下载 `{baseWeight}` |");
        text.AppendLine(hasDataset
            ? classify
                ? "| `dataset/classification/` | 本次训练的数据集（按类别分目录的裁剪图），`split-report.json` 为划分报告 |"
                : "| `dataset/` | 本次训练的数据集（YOLO 格式：images/、labels/、data.yaml），`split-report.json` 为划分报告 |"
            : "| `dataset/` | 未保存（旧版本训练）；可在 MaskFlow 数据集导出页重新导出 |");
        text.AppendLine("| `runs/train/weights/best.pt` | 验证集上效果最好的权重（推荐使用） |");
        text.AppendLine("| `runs/train/weights/last.pt` | 最后一轮的权重（可继续训练） |");
        text.AppendLine("| `runs/train/weights/best.onnx` | ONNX 导出，可在 MaskFlow 上传为自定义模型用于自动标注 |");
        text.AppendLine("| `runs/train/results.csv`、`results.png` | 每一轮的损失和指标及曲线 |");
        text.AppendLine("| `runs/train/args.yaml` | Ultralytics 记录的完整训练参数 |");
        text.AppendLine("| `runs/train/*.jpg` | 训练样本（数据增强后）和标签分布图 |");
        text.AppendLine("| `runs/val/`" + (hasTest ? "、`runs/test/`" : "") + " | 评估结果：混淆矩阵、PR/F1/P/R 曲线、预测效果对比（`*_labels.jpg` 为标注，`*_pred.jpg` 为预测） |");
        text.AppendLine("| `runs/metrics.json` | 汇总指标（总体与逐类） |");
        text.AppendLine("| `runs/train.log`、`runs/environment.json` | 训练日志、软硬件环境 |");
        text.AppendLine("| `train.py`、`predict.py`、`requirements.txt` | 复现训练、用模型预测、依赖 |");
        text.AppendLine("| `training.json` | 本次训练的完整记录（参数、指标、每轮历史） |").AppendLine();

        text.AppendLine("## 复现训练").AppendLine();
        text.AppendLine("```bash");
        text.AppendLine("pip install -r requirements.txt   # 先按 https://pytorch.org 安装与显卡匹配的 PyTorch");
        text.AppendLine("python train.py                   # 默认第一块 GPU；无 GPU：python train.py --device cpu");
        text.AppendLine("```").AppendLine();
        text.AppendLine("结果写入 `rerun/`。即使参数相同，GPU、驱动和库版本不同也会让指标有小幅差异。").AppendLine();
        text.AppendLine("## 使用模型").AppendLine();
        text.AppendLine("```bash");
        text.AppendLine("python predict.py 图片或文件夹路径   # 结果保存在 predictions/");
        text.AppendLine("```");
        return text.ToString();
    }

    void AppendMetrics(StringBuilder text)
    {
        if (run.Metrics is not { ValueKind: JsonValueKind.Object } metrics) return;
        var suffix = run.Task == "segment" ? "(M)" : "(B)";
        var rows = classify
            ? new[] { ("Top-1 准确率", "metrics/accuracy_top1"), ("Top-5 准确率", "metrics/accuracy_top5") }
            : [("mAP50-95", $"metrics/mAP50-95{suffix}"), ("mAP50", $"metrics/mAP50{suffix}"), ("精确率", $"metrics/precision{suffix}"), ("召回率", $"metrics/recall{suffix}")];
        string Value(string split, string key) =>
            metrics.TryGetProperty(split, out var part) && part.TryGetProperty("overall", out var overall)
            && overall.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? F(value.GetDouble()) : "—";

        text.AppendLine("## 评估结果").AppendLine();
        text.AppendLine(hasTest ? "| 指标 | 验证集 | 测试集 |\n| --- | --- | --- |" : "| 指标 | 验证集 |\n| --- | --- |");
        foreach (var (label, key) in rows)
        {
            text.AppendLine(hasTest ? $"| {label} | {Value("val", key)} | {Value("test", key)} |" : $"| {label} | {Value("val", key)} |");
        }

        text.AppendLine();
        var split = hasTest ? "test" : "val";
        if (metrics.TryGetProperty(split, out var part) && part.TryGetProperty("perClass", out var perClass) && perClass.GetArrayLength() > 0)
        {
            text.AppendLine($"逐类结果（{(hasTest ? "测试集" : "验证集")}）：").AppendLine();
            text.AppendLine("| 类别 | mAP50 | mAP50-95 |").AppendLine("| --- | --- | --- |");
            foreach (var row in perClass.EnumerateArray())
            {
                string Cell(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? F(value.GetDouble()) : "—";
                text.AppendLine($"| {row.GetProperty("class")} | {Cell("mAP50")} | {Cell("mAP50-95")} |");
            }

            text.AppendLine();
        }

        if (!hasTest) text.AppendLine("> 没有测试集：验证集用于挑选最佳权重，指标会略偏乐观。").AppendLine();
    }

    string DataExpression => classify
        ? "str(ROOT / \"dataset\" / \"classification\")"
        : "local_data_yaml()";

    public string TrainScript() => $$""""
        """Re-run MaskFlow training "{{run.Name}}" ({{run.Id}}) with the same base weight, dataset and parameters.

        Usage: python train.py [--device 0|0,1|cpu] [--epochs N]
        """
        import argparse
        import re
        from pathlib import Path

        from ultralytics import YOLO

        ROOT = Path(__file__).resolve().parent
        BASE_WEIGHT = ROOT / "base" / "{{baseWeight}}"
        OUTPUT = ROOT / "rerun"


        def local_data_yaml() -> str:
            """data.yaml uses `path: .`; point it at this folder so training works from any directory."""
            source = ROOT / "dataset" / "data.yaml"
            text = re.sub(r"(?m)^path:.*$", f"path: {(ROOT / 'dataset').as_posix()}", source.read_text(encoding="utf-8"))
            target = ROOT / "dataset" / "data.local.yaml"
            target.write_text(text, encoding="utf-8")
            return str(target)


        def main() -> None:
            parser = argparse.ArgumentParser(description=__doc__)
            parser.add_argument("--device", default=None, help="GPU id(s) such as 0 or 0,1, or cpu")
            parser.add_argument("--epochs", type=int, default={{run.Request.Epochs}})
            args = parser.parse_args()
            data = {{DataExpression}}

            model = YOLO(str(BASE_WEIGHT))
            model.train(
                data=data,
                epochs=args.epochs,
                imgsz={{run.Request.ImageSize}},
                batch={{run.Request.Batch}},
                patience={{run.Request.Patience}},
                device=args.device,
                project=str(OUTPUT),
                name="train",
                exist_ok=True,
                plots=True,
            )

            best = YOLO(str(OUTPUT / "train" / "weights" / "best.pt"))
            print(best.val(data=data, split="val", project=str(OUTPUT), name="val", exist_ok=True).results_dict)
        {{(hasTest ? "    print(best.val(data=data, split=\"test\", project=str(OUTPUT), name=\"test\", exist_ok=True).results_dict)\n" : "")}}    best.export(format="onnx", imgsz={{run.Request.ImageSize}})
            print(f"完成：{OUTPUT / 'train' / 'weights'}")


        if __name__ == "__main__":
            main()

        """";

    public string PredictScript() => $$""""
        """Predict with the model trained in MaskFlow run "{{run.Name}}" ({{run.Id}}).

        Usage: python predict.py [image, folder or video] [--conf 0.25]
        """
        import argparse
        from pathlib import Path

        from ultralytics import YOLO

        ROOT = Path(__file__).resolve().parent
        WEIGHTS = ROOT / "runs" / "train" / "weights" / "best.pt"


        def main() -> None:
            parser = argparse.ArgumentParser(description=__doc__)
            parser.add_argument("source", nargs="?", default=str(ROOT / "dataset" / {{(classify ? "\"classification\" / \"val\"" : "\"images\" / \"val\"")}}))
            parser.add_argument("--conf", type=float, default=0.25)
            args = parser.parse_args()
            model = YOLO(str(WEIGHTS))
            model.predict(source=args.source, imgsz={{run.Request.ImageSize}}, conf=args.conf, save=True,
                          project=str(ROOT / "predictions"), name="run", exist_ok=True)
            print(f"结果保存在 {ROOT / 'predictions' / 'run'}")


        if __name__ == "__main__":
            main()

        """";

    public string Requirements()
    {
        var ultralytics = Env("ultralytics");
        return $"""
            # PyTorch: install the build matching your GPU from https://pytorch.org first
            # (this run used PyTorch {(Env("torch") is { Length: > 0 } torch ? torch : "unknown")}, CUDA {(Env("cuda") is { Length: > 0 } cuda ? cuda : "unknown")}).
            {(ultralytics.Length > 0 ? $"ultralytics=={ultralytics}" : "ultralytics>=8.4.0")}
            onnx>=1.12.0
            onnxslim>=0.1.71

            """;
    }

    public string Metadata() => JsonSerializer.Serialize(new
    {
        run.Id,
        run.Name,
        project = projectName,
        run.ProjectId,
        run.Task,
        run.Format,
        parameters = run.Request,
        run.Status,
        run.Epoch,
        run.ImageCount,
        headline = MaskFlowStore.TrainingHeadline(run),
        evaluation = run.Metrics,
        history = run.History,
        environment,
        run.ModelId,
        run.Error,
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt,
        files = run.Artifacts.Select(x => new { x.Name, x.Size })
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
