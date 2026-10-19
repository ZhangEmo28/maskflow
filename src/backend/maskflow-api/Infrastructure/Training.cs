using System.Text.Json;
using Amazon.S3.Model;

public sealed record TrainerWeight(string Name, long Size, string Task);

public sealed partial class MaskFlowStore
{
    public static readonly string[] ActiveTrainingStatuses = ["queued", "preparing", "training", "evaluating", "exporting"];
    // Statuses in which the trainer service owns the run and the worker polls it.
    public static readonly string[] RemoteTrainingStatuses = ["training", "evaluating", "exporting"];
    // Kept after a run; last.pt is left out to save space.
    public static readonly string[] TrainingArtifactNames =
        ["best.pt", "best.onnx", "results.csv", "results.png", "confusion_matrix.png", "confusion_matrix_normalized.png", "metrics.json"];

    public static bool IsActiveTraining(TrainingRun run) => ActiveTrainingStatuses.Contains(run.Status);

    // The dataset export a base weight trains on, from the weight's task and the project type.
    public static string TrainingFormat(string task, string? dataType) => task switch
    {
        "detect" => "yolo-detect",
        "segment" when NormalizeDataType(dataType) == "segmentation" => "yolo-segment",
        "segment" => throw new BadHttpRequestException("分割权重需要分割项目；检测项目请选择检测权重（如 yolo11n.pt）。", 400),
        "classify" => "classification-crops",
        _ => throw new BadHttpRequestException($"暂不支持 {task} 类型的权重，目前支持检测、分割和分类。", 400)
    };

    public static void ValidateTrainingRequest(TrainingRequest request)
    {
        if (request.Epochs is < 1 or > 1000) throw new BadHttpRequestException("训练轮数需在 1–1000 之间。", 400);
        if (request.ImageSize is < 32 or > 4096) throw new BadHttpRequestException("训练图片尺寸需在 32–4096 之间。", 400);
        if (request.Batch is 0 or < -1 or > 512) throw new BadHttpRequestException("批大小需在 1–512 之间，-1 表示按显存自动选择。", 400);
        if (request.Patience is < 0 or > 1000) throw new BadHttpRequestException("早停轮数需在 0–1000 之间。", 400);
    }

    public async Task<TrainingRun> CreateTrainingRunAsync(int actorId, ProjectAccess access, TrainingRequest request, TrainerWeight weight)
    {
        ValidateTrainingRequest(request);
        var split = request.Split ?? new SplitConfig(70, 20, 10);
        DatasetSplitter.Validate(split);
        if (split.Val <= 0) throw new BadHttpRequestException("训练需要验证集，请把验证集比例设为大于 0。", 400);
        var format = TrainingFormat(weight.Task, access.Project.DataType);
        var readiness = await CheckTrainingDataAsync(access, request with { Split = split }, weight.Task);
        if (!readiness.Ok)
        {
            var advice = readiness.Suggestions.Take(3).ToList();
            throw new BadHttpRequestException("数据还不能训练：" + string.Join(" ", readiness.Blockers)
                + (advice.Count > 0 ? " 建议：" + string.Join(" ", advice) : ""), 400);
        }

        TrainingRun? run = null;
        await SaveAsync(() =>
        {
            if (State.TrainingRuns.Any(x => x.ProjectId == access.Project.Id && IsActiveTraining(x)))
            {
                throw new BadHttpRequestException("这个项目已有训练在排队或进行中，请等它结束或先取消。", 409);
            }

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? $"{access.Project.Name} · {Path.GetFileNameWithoutExtension(weight.Name)}"
                : request.Name.Trim();
            run = new TrainingRun("train_" + Util.Id(), access.OwnerId, access.Project.Id, actorId, name[..Math.Min(name.Length, 200)],
                weight.Task, format, request with { BaseWeight = weight.Name, Split = split, Name = null }, "queued", 0, [], null, [], null,
                readiness.Images, null, DateTimeOffset.UtcNow, null, null);
            State.TrainingRuns.Add(run);
        });
        return run!;
    }

    // Same selection, near-duplicate grouping and split as a dataset export; training needs images in train and val.
    async Task<DatasetExportPlan> PlanTrainingDatasetAsync(int ownerId, string projectId, string format, TrainingRequest request)
    {
        await EnsurePerceptualHashesAsync(ownerId, projectId);
        var plan = DatasetExportPlan.Create(State, ownerId, new ExportRequest(projectId, null, request.Split, format, request.Seed, request.ConfirmedOnly));
        plan.ValidateExport();
        var assignments = DatasetSplitter.Assign(plan.Samples, request.Split!, request.Seed, plan.GroupOf);
        var empty = assignments.Values.All(x => x != "train") ? "训练集" : assignments.Values.All(x => x != "val") ? "验证集" : null;
        if (empty is not null)
        {
            throw new BadHttpRequestException($"可训练的已标注图片只有 {plan.Files.Count} 张，划分后{empty}为空。请多标注一些图片或调整划分比例。", 400);
        }

        return plan;
    }

    // Writes the dataset as it is now (annotations may have changed since the run was queued) to a temp zip.
    public async Task<(string Path, int Images)> WriteTrainingDatasetAsync(TrainingRun run)
    {
        var plan = await PlanTrainingDatasetAsync(run.UserId, run.ProjectId, run.Format, run.Request);
        var path = Path.Combine(Path.GetTempPath(), $"maskflow-{run.Id}-{Util.Id()}.zip");
        try
        {
            await WriteDatasetArchiveAsync(path, plan, run.Request.Split!, run.Request.Seed);
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        return (path, plan.Files.Count);
    }

    public TrainingRun? GetTrainingRun(string id) => State.TrainingRuns.FirstOrDefault(x => x.Id == id);

    // Applies `update` to the current row; returning null leaves it unchanged. Returns the stored row, or null when skipped.
    public async Task<TrainingRun?> UpdateTrainingRunAsync(string id, Func<TrainingRun, TrainingRun?> update)
    {
        TrainingRun? result = null;
        await SaveAsync(() =>
        {
            var index = State.TrainingRuns.FindIndex(x => x.Id == id);
            if (index < 0) return;
            result = update(State.TrainingRuns[index]);
            if (result is not null) State.TrainingRuns[index] = result;
        });
        return result;
    }

    public Task<TrainingRun?> FinishTrainingRunAsync(string id, string status, string? error, List<Dictionary<string, double>>? history = null) =>
        UpdateTrainingRunAsync(id, run => IsActiveTraining(run)
            ? run with
            {
                Status = status, Error = error, FinishedAt = DateTimeOffset.UtcNow,
                History = history ?? run.History, Epoch = history?.Count ?? run.Epoch
            }
            : null);

    // A dataset upload cut short by a restart is simply retried.
    public Task RequeueInterruptedTrainingsAsync() =>
        SaveAsync(() =>
        {
            for (var i = 0; i < State.TrainingRuns.Count; i++)
            {
                if (State.TrainingRuns[i].Status == "preparing") State.TrainingRuns[i] = State.TrainingRuns[i] with { Status = "queued", StartedAt = null };
            }
        });

    public async Task<string> PersistTrainingArtifactAsync(TrainingRun run, string name, string localPath)
    {
        var key = $"users/{run.UserId}/trainings/{run.Id}/{name}";
        if (UseMinio)
        {
            await s3Client.Value.PutObjectAsync(new PutObjectRequest
            {
                BucketName = MinioBucket,
                Key = key,
                FilePath = localPath,
                ContentType = ArtifactContentType(name)
            });
            return $"minio://{MinioBucket}/{key}";
        }

        var directory = Path.Combine(StorageRoot, run.UserId.ToString(), "trainings", run.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.Copy(localPath, path, overwrite: true);
        return path;
    }

    public static string ArtifactContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".csv" => "text/csv",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    // Artifacts count against the project owner's storage. A run cancelled or deleted while its
    // artifacts were being collected keeps nothing.
    public async Task<TrainingRun?> CompleteTrainingRunAsync(string id, List<TrainingArtifact> artifacts, JsonElement? metrics, List<Dictionary<string, double>> history)
    {
        TrainingRun? result = null;
        await SaveAsync(() =>
        {
            deleteOnRollback.AddRange(artifacts.Select(x => x.Path));
            var index = State.TrainingRuns.FindIndex(x => x.Id == id);
            if (index < 0 || !IsActiveTraining(State.TrainingRuns[index]))
            {
                deleteAfterCommit.AddRange(artifacts.Select(x => x.Path));
                return;
            }

            var run = State.TrainingRuns[index];
            var owner = GetUser(run.UserId)!;
            var bytes = artifacts.Sum(x => x.Size);
            EnsureStorageAvailable(owner, bytes);
            result = run with
            {
                Status = "completed", Artifacts = artifacts, Metrics = metrics, History = history, Epoch = history.Count,
                Error = null, FinishedAt = DateTimeOffset.UtcNow
            };
            State.TrainingRuns[index] = result;
            ReplaceUser(owner with { UsedBytes = owner.UsedBytes + bytes });
        });
        return result;
    }

    // Registers the exported ONNX as a project model, so it can be used for auto-annotation right away.
    public async Task<AiModel> AddTrainedModelAsync(TrainingRun run, string onnxPath, List<string> classNames)
    {
        var owner = GetUser(run.UserId) ?? throw new BadHttpRequestException("项目所有者不存在。", 404);
        await using var stream = File.OpenRead(onnxPath);
        var upload = new FormFile(stream, 0, stream.Length, "file", "best.onnx");
        var model = await AddAiModelAsync(owner, upload, $"{run.Name}（{run.CreatedAt:yyyy-MM-dd HH:mm} 训练）", run.ProjectId);
        if (classNames.Count > 0) await UpdateAiModelClassNamesAsync(model.Id, classNames);
        return model;
    }

    public async Task<bool> DeleteTrainingRunAsync(string projectId, string id)
    {
        var deleted = false;
        await SaveAsync(() =>
        {
            var run = State.TrainingRuns.FirstOrDefault(x => x.Id == id && x.ProjectId == projectId);
            if (run is null) return;
            if (IsActiveTraining(run)) throw new BadHttpRequestException("训练还在进行中，请先取消。", 409);
            RemoveTrainingRunLocked(run);
            deleted = true;
        });
        return deleted;
    }

    // Registered models are independent copies and stay.
    void RemoveTrainingRunLocked(TrainingRun run)
    {
        State.TrainingRuns.Remove(run);
        deleteAfterCommit.AddRange(run.Artifacts.Select(x => x.Path));
        var owner = GetUser(run.UserId);
        if (owner is not null) ReplaceUser(owner with { UsedBytes = Math.Max(0, owner.UsedBytes - run.Artifacts.Sum(x => x.Size)) });
    }

    public static object TrainingSummary(TrainingRun run) => new
    {
        run.Id,
        run.ProjectId,
        run.Name,
        run.Task,
        run.Format,
        BaseWeight = run.Request.BaseWeight,
        run.Request,
        run.Status,
        run.Epoch,
        Epochs = run.Request.Epochs,
        run.ImageCount,
        run.ModelId,
        run.Error,
        Headline = TrainingHeadline(run),
        Artifacts = run.Artifacts.Select(x => new { x.Name, x.Size }),
        run.CreatedBy,
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt
    };

    // The main validation metrics, flattened for lists and agents: mAP for detect/segment, accuracy for classify.
    public static Dictionary<string, double> TrainingHeadline(TrainingRun run)
    {
        var headline = new Dictionary<string, double>();
        if (run.Metrics is not { ValueKind: JsonValueKind.Object } metrics
            || !metrics.TryGetProperty("val", out var val)
            || !val.TryGetProperty("overall", out var overall)
            || overall.ValueKind != JsonValueKind.Object) return headline;

        var suffix = run.Task == "segment" ? "(M)" : "(B)";
        foreach (var (key, name) in new[]
                 {
                     ($"metrics/mAP50{suffix}", "mAP50"), ($"metrics/mAP50-95{suffix}", "mAP50-95"),
                     ($"metrics/precision{suffix}", "precision"), ($"metrics/recall{suffix}", "recall"),
                     ("metrics/accuracy_top1", "top1"), ("metrics/accuracy_top5", "top5")
                 })
        {
            if (overall.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number) headline[name] = Math.Round(value.GetDouble(), 4);
        }

        return headline;
    }
}
