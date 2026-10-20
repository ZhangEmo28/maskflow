using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class TrainingTools(McpCaller caller, MaskFlowStore store, TrainingWorker trainer)
{
    // Plot name -> candidate files (Ultralytics names vary slightly between versions and tasks).
    static readonly Dictionary<string, string[]> Plots = new()
    {
        ["confusion_matrix_normalized"] = ["val/confusion_matrix_normalized.png"],
        ["confusion_matrix"] = ["val/confusion_matrix.png"],
        ["results"] = ["train/results.png"],
        ["pr_curve"] = ["val/BoxPR_curve.png", "val/MaskPR_curve.png", "val/PR_curve.png"],
        ["f1_curve"] = ["val/BoxF1_curve.png", "val/MaskF1_curve.png", "val/F1_curve.png"],
        ["val_predictions"] = ["val/val_batch0_pred.jpg"],
        ["val_labels"] = ["val/val_batch0_labels.jpg"],
        ["label_distribution"] = ["train/labels.jpg"],
        ["train_batch"] = ["train/train_batch0.jpg"]
    };

    static TrainingArtifact? Plot(TrainingRun run, string plot) =>
        Plots.TryGetValue(plot, out var candidates) ? candidates.Select(x => MaskFlowStore.FindArtifact(run, x)).FirstOrDefault(x => x is not null) : null;

    [McpServerTool(Name = "start_training", Title = "开始训练", Idempotent = false)]
    [Description("Train a YOLO model on the project's current annotations, starting from a pre-downloaded base weight (list_base_weights). The run trains, evaluates on val (and test if present) and exports best.pt + ONNX; detect/segment results are registered as a project model for auto annotation. Queued in the background — poll get_training. Owner/reviewer only.")]
    public Task<object> StartTraining(
        string project_id,
        [Description("File name from list_base_weights, e.g. yolo11n.pt")] string base_weight,
        int epochs = 50,
        [Description("Training image size")] int image_size = 640,
        [Description("Batch size; -1 picks automatically from GPU memory")] int batch = 16,
        [Description("Stop early after this many epochs without improvement")] int patience = 20,
        [Description("Percent of images for train/val/test; must sum to 100 and val > 0")] int train = 70, int val = 20, int test = 10,
        [Description("Only use human-confirmed targets")] bool confirmed_only = false,
        string? name = null) => McpCaller.RunAsync<object>(async () =>
    {
        var access = caller.Project(project_id, Permissions.Export);
        var weights = await trainer.ListWeightsAsync(caller.Aborted);
        var weight = weights.FirstOrDefault(x => x.Name == base_weight)
                     ?? throw new McpException($"没有基础权重 {base_weight}。可用：{string.Join("、", weights.Select(x => $"{x.Name}（{x.Task}）"))}");
        var run = await store.CreateTrainingRunAsync(caller.UserId, access,
            new TrainingRequest(base_weight, epochs, image_size, batch, patience, new SplitConfig(train, val, test), confirmed_only, Name: name), weight);
        trainer.Wake();
        return new { trainingId = run.Id, run.Name, run.Status, run.Format, images = run.ImageCount, next = "用 get_training 查看进度", webUrl = $"{McpCaller.PublicUrl}/train.html" };
    });

    [McpServerTool(Name = "check_training_data", Title = "检查训练数据", ReadOnly = true, Idempotent = true)]
    [Description("Check whether the project's annotations are ready for training, before starting: minimum image counts (blockers), how each class actually splits into train/val/test, class balance, background images, unconfirmed share, and concrete suggestions of how many more images of which kind to add. start_training refuses data with blockers.")]
    public Task<object> CheckTrainingData(
        string project_id,
        [Description("Base weight file name (decides detect/segment/classify); optional")] string? base_weight = null,
        int train = 70, int val = 20, int test = 10, bool confirmed_only = false) => McpCaller.RunAsync<object>(async () =>
    {
        var access = caller.Project(project_id, Permissions.Read);
        var task = base_weight is null
            ? MaskFlowStore.NormalizeDataType(access.Project.DataType) == "segmentation" ? "segment" : "detect"
            : TrainingReadiness.TaskFromWeightName(base_weight);
        return await store.CheckTrainingDataAsync(access, new TrainingRequest(base_weight ?? "", Split: new SplitConfig(train, val, test), ConfirmedOnly: confirmed_only), task);
    });

    [McpServerTool(Name = "list_trainings", Title = "训练记录", ReadOnly = true, Idempotent = true)]
    [Description("Training runs of a project, newest first, with status and headline validation metrics.")]
    public object ListTrainings(string project_id) => McpCaller.Run(() =>
    {
        caller.Project(project_id, Permissions.Read);
        return new
        {
            trainings = store.State.TrainingRuns.Where(x => x.ProjectId == project_id).OrderByDescending(x => x.CreatedAt)
                .Select(x => new
                {
                    x.Id, x.Name, x.Status, x.Task, baseWeight = x.Request.BaseWeight, x.Epoch, epochs = x.Request.Epochs,
                    metrics = MaskFlowStore.TrainingHeadline(x), x.ModelId, x.Error, x.CreatedAt, x.FinishedAt
                })
        };
    });

    [McpServerTool(Name = "get_training", Title = "训练详情", ReadOnly = true, Idempotent = true)]
    [Description("Progress and results of a training run: status, epoch, the loss/mAP curve (sampled), final validation and test metrics with per-class mAP, the registered model id and the saved artifacts. Explain the numbers to the user: mAP50-95 is the main score, compare val vs test for overfitting, low per-class scores point at labels that need more or better data.")]
    public object GetTraining(string training_id) => McpCaller.Run(() =>
    {
        var run = caller.Training(training_id, Permissions.Read);
        // At most ~20 evenly spaced epochs keep the response small while showing the trend.
        var step = Math.Max(1, (int)Math.Ceiling(run.History.Count / 20.0));
        var curve = run.History.Where((_, i) => i % step == 0 || i == run.History.Count - 1)
            .Select(row => row.Where(x => x.Key == "epoch" || x.Key.Contains("loss") || x.Key.StartsWith("metrics/"))
                .ToDictionary(x => x.Key, x => Math.Round(x.Value, 4)));
        return new
        {
            run.Id, run.Name, run.Status, run.Task, run.Format, run.Request, run.Epoch, run.ImageCount, run.Error,
            headline = MaskFlowStore.TrainingHeadline(run),
            curve,
            evaluation = run.Metrics,
            run.ModelId,
            artifacts = run.Artifacts.Select(x => new { x.Name, x.Size }),
            plots = Plots.Keys.Where(x => Plot(run, x) is not null),
            package = MaskFlowStore.IsActiveTraining(run) ? null : "训练包（基础权重、数据集、训练脚本、全部训练结果）可在训练页下载",
            run.CreatedAt, run.StartedAt, run.FinishedAt,
            downloadPage = $"{McpCaller.PublicUrl}/train.html"
        };
    });

    [McpServerTool(Name = "cancel_training", Title = "取消训练", Destructive = true, Idempotent = true)]
    [Description("Cancel a queued or running training. Progress is not kept.")]
    public Task<object> CancelTraining(string training_id) => McpCaller.RunAsync<object>(async () =>
    {
        var run = caller.Training(training_id, Permissions.Export);
        if (!MaskFlowStore.IsActiveTraining(run)) throw new McpException($"训练已经结束（{run.Status}）。");
        var cancelled = await trainer.CancelAsync(training_id, caller.Aborted);
        return new { trainingId = training_id, status = cancelled?.Status ?? store.GetTrainingRun(training_id)?.Status };
    });

    [McpServerTool(Name = "view_training_plot", Title = "查看训练图表", ReadOnly = true, Idempotent = true)]
    [Description("Image of a finished training's plot: confusion_matrix_normalized (default; which classes get confused with each other or with background), confusion_matrix (counts), results (training curves), pr_curve, f1_curve, val_predictions (model predictions on validation images) with val_labels (the same images with the human labels, to compare), label_distribution, train_batch (augmented training samples). get_training lists which exist.")]
    public Task<CallToolResult> ViewTrainingPlot(string training_id, string plot = "confusion_matrix_normalized") => McpCaller.RunAsync(async () =>
    {
        var run = caller.Training(training_id, Permissions.Read);
        if (!Plots.ContainsKey(plot)) throw new McpException($"plot 只能是：{string.Join("、", Plots.Keys)}");
        var artifact = Plot(run, plot) ?? throw new McpException("这次训练没有这张图（训练未完成、被删除，或该任务类型不产生这张图）。");
        await using var stream = await store.OpenStoredObjectAsync(artifact.Path);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, caller.Aborted);
        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(buffer.ToArray(), MaskFlowStore.ArtifactContentType(artifact.Name)),
                new TextContentBlock { Text = $"{run.Name} 的 {plot}" }
            ]
        };
    });
}
