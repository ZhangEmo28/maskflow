using System.ComponentModel;
using System.Text;
using MaskFlow.Api.Infrastructure.Security;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed record NewTarget(
    [property: Description("Project label")] string Label,
    [property: Description("Normalized [x1, y1, x2, y2] in 0-1; optional when polygon is given")] double[]? Box = null,
    [property: Description("Normalized polygon [x1, y1, x2, y2, ...] (segmentation projects)")] double[]? Polygon = null);

public sealed record TargetChange(
    [property: Description("Target id from get_annotations")] string Id,
    [property: Description("New label")] string? Label = null,
    [property: Description("New normalized [x1, y1, x2, y2]")] double[]? Box = null,
    [property: Description("New normalized polygon")] double[]? Polygon = null);

[McpServerToolType]
public sealed class AnnotationTools(McpCaller caller, MaskFlowStore store, AutoAnnotationService annotator, BatchAnnotationWorker batches, SamInferenceGate samGate)
{
    static readonly string[] ImageFilters = ["all", "unannotated", "unconfirmed", "confirmed", "empty", "submitted", "rejected", "approved"];

    [McpServerTool(Name = "list_images", Title = "列出图片", ReadOnly = true, Idempotent = true)]
    [Description("List images of a project with their annotation state. status: all | unannotated | unconfirmed (has AI targets awaiting review) | confirmed (all targets confirmed) | empty (annotated, no targets) | submitted | rejected | approved. Unconfirmed images are sorted lowest-confidence first.")]
    public object ListImages(string project_id, string status = "all", int limit = 50, int offset = 0) => McpCaller.Run(() =>
    {
        if (!ImageFilters.Contains(status)) throw new McpException($"status 只能是：{string.Join(", ", ImageFilters)}");
        var access = caller.Project(project_id, Permissions.Read);
        var sets = store.State.AnnotationSets.Where(x => x.UserId == access.OwnerId).ToDictionary(x => x.FileId);
        var rows = store.State.Files.Where(x => x.UserId == access.OwnerId && x.ProjectId == project_id && x.Kind == "image")
            .OrderBy(x => x.Id)
            .Select(file =>
            {
                var set = sets.GetValueOrDefault(file.Id);
                var unconfirmed = set?.Annotations.Where(x => !x.Confirmed).ToList() ?? [];
                return new
                {
                    fileId = file.Id,
                    file.Name,
                    annotated = set is not null,
                    targets = set?.Annotations.Count ?? 0,
                    unconfirmed = unconfirmed.Count,
                    minConfidence = unconfirmed.Count == 0 ? (double?)null : Math.Round(unconfirmed.Min(x => x.Confidence), 3),
                    labels = set?.Annotations.Where(MaskFlowStore.IsExportableAnnotation).GroupBy(x => x.Label!).ToDictionary(x => x.Key, x => x.Count()),
                    reviewStatus = set?.ReviewStatus
                };
            })
            .Where(x => status switch
            {
                "unannotated" => !x.annotated,
                "unconfirmed" => x.unconfirmed > 0,
                "confirmed" => x.targets > 0 && x.unconfirmed == 0,
                "empty" => x.annotated && x.targets == 0,
                "submitted" or "rejected" or "approved" => x.reviewStatus == status,
                _ => true
            })
            .ToList();
        if (status == "unconfirmed") rows = rows.OrderBy(x => x.minConfidence).ToList();
        return new { total = rows.Count, offset, images = rows.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 200)) };
    });

    [McpServerTool(Name = "get_annotations", Title = "读取标注", ReadOnly = true, Idempotent = true)]
    [Description("Annotations of one image: each target's id, label, normalized box [x1, y1, x2, y2], polygon size, AI confidence and whether a human confirmed it.")]
    public object GetAnnotations(int file_id) => McpCaller.Run(() =>
    {
        var (file, ownerId, _) = caller.File(file_id, Permissions.Read);
        var set = store.GetAnnotationSet(ownerId, file_id);
        return new
        {
            fileId = file.Id,
            file.Name,
            width = set?.Width,
            height = set?.Height,
            reviewStatus = set?.ReviewStatus,
            reviewComment = set?.ReviewComment,
            targets = (set?.Annotations ?? []).Select((x, index) => Describe(x, index + 1))
        };
    });

    [McpServerTool(Name = "view_image", Title = "查看图片与标注", ReadOnly = true, Idempotent = true)]
    [Description("Returns the image with its annotations drawn on it so you can judge them visually. Targets are numbered 1..n (legend in the text part): solid outline = human-confirmed, dashed = unconfirmed AI result, grey = no label. With model_id, that model's predictions are drawn in yellow with dark number tags (uses 1 AI quota).")]
    public Task<CallToolResult> ViewImage(
        int file_id,
        [Description("Draw the saved annotations")] bool show_annotations = true,
        [Description("Optionally overlay predictions of this model (from list_models)")] string? model_id = null,
        [Description("Longest side of the returned image in pixels (256-2048)")] int max_side = 1024) => McpCaller.RunAsync(async () =>
    {
        var (file, ownerId, access) = caller.File(file_id, model_id is null ? Permissions.Read : Permissions.Edit);
        var labels = file.ProjectId is null ? [] : store.GetProjectLabels(ownerId, file.ProjectId);
        var annotations = show_annotations ? store.GetAnnotationSet(ownerId, file_id)?.Annotations ?? [] : [];
        List<AnnotationItem>? predictions = null;
        if (model_id is not null)
        {
            predictions = await PredictAsync(ownerId, file, AutoAnnotationOptions.Create("model", 0.25, null, model_id));
        }

        var bytes = await ReadAsync(file);
        var rendered = AnnotationRenderer.Render(bytes, annotations, labels, Math.Clamp(max_side, 256, 2048), predictions);
        var legend = new StringBuilder($"图片 {file.Name}（id {file.Id}）");
        legend.Append(annotations.Count == 0 ? "，没有标注。" : $"，{annotations.Count} 个目标：\n");
        for (var i = 0; i < annotations.Count; i++) legend.AppendLine(Legend(annotations[i], i + 1));
        if (predictions is not null)
        {
            legend.AppendLine($"模型预测（黄色）{predictions.Count} 个：");
            for (var i = 0; i < predictions.Count; i++) legend.AppendLine(Legend(predictions[i], i + 1, prediction: true));
        }

        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(rendered, "image/jpeg"),
                new TextContentBlock { Text = legend.ToString().TrimEnd() }
            ]
        };
    });

    [McpServerTool(Name = "auto_annotate_image", Title = "AI 标注一张图片", Idempotent = false)]
    [Description("Run AI annotation on one image and save the result (human-confirmed targets are kept). mode: concepts = SAM 3 finds every project label by its text prompt (best default); everything = segment everything, all masks get default_label; model = a trained/uploaded ONNX model (model_id). Uses 1 AI quota. Check the result with view_image.")]
    public Task<object> AutoAnnotateImage(
        int file_id,
        string mode = "concepts",
        [Description("Required for mode=model")] string? model_id = null,
        [Description("Confidence threshold 0.01-0.95")] double conf = 0.25,
        [Description("Label for every mask in mode=everything")] string? default_label = null) => McpCaller.RunAsync<object>(async () =>
    {
        var (file, ownerId, _) = caller.File(file_id, Permissions.Edit);
        store.EnsureAiQuotaAvailable(store.GetUser(ownerId)!);
        var options = AutoAnnotationOptions.Create(mode, conf, default_label, model_id);
        await using var slot = await samGate.WaitAsync(caller.Aborted);
        var set = await annotator.RunAsync(ownerId, file, options, recordTask: true, caller.Aborted);
        return new
        {
            fileId = file_id,
            mode = options.Mode,
            targets = set.Annotations.Count,
            byLabel = set.Annotations.GroupBy(x => x.Label ?? "(未分配)").ToDictionary(x => x.Key, x => x.Count()),
            next = "用 view_image 查看效果"
        };
    });

    [McpServerTool(Name = "auto_annotate_project", Title = "批量 AI 标注", Idempotent = false)]
    [Description("Queue AI annotation for many images of a project as a background task (same modes as auto_annotate_image). scope: unannotated (default) or all (re-run; confirmed targets are kept). Returns a task id; poll get_task. Uses 1 AI quota per image.")]
    public Task<object> AutoAnnotateProject(string project_id, string mode = "concepts", string scope = "unannotated", string? model_id = null,
        double conf = 0.25, string? default_label = null) => McpCaller.RunAsync<object>(async () =>
    {
        var access = caller.Project(project_id, Permissions.Edit);
        var task = await batches.QueueAsync(access, new AnnotationBatchRequest(project_id, conf, default_label, mode, model_id, scope == "all" ? "all" : "unannotated"));
        return new { taskId = task.Id, task.Title, task.Status, images = task.ImageCount, next = "用 get_task 查看进度" };
    });

    [McpServerTool(Name = "get_task", Title = "任务进度", ReadOnly = true, Idempotent = true)]
    [Description("Status and progress of a background task (batch annotation, video frames, propagation).")]
    public object GetTask(string task_id) => McpCaller.Run(() =>
    {
        var task = caller.Task(task_id);
        var progress = BatchProgress.From(task.Result);
        return new
        {
            task.Id, task.Type, task.Title, task.Status, progress = Math.Round(task.Progress, 3), total = task.ImageCount,
            processed = progress.Processed, succeeded = progress.Succeeded, failed = progress.Failed, lastError = progress.LastError ?? task.ErrorMessage,
            task.CreatedAt, task.FinishedAt
        };
    });

    [McpServerTool(Name = "edit_annotations", Title = "修改标注", Destructive = true, Idempotent = false)]
    [Description("Fix one image's annotations: add targets, change a target's label/box/polygon, or delete targets (ids from get_annotations). Coordinates are normalized 0-1; boxes are [x1, y1, x2, y2]. Added or changed targets are saved as unconfirmed for human review.")]
    public Task<object> EditAnnotations(
        int file_id,
        [Description("Targets to add")] NewTarget[]? add = null,
        [Description("Targets to change")] TargetChange[]? update = null,
        [Description("Ids of targets to delete")] string[]? delete = null) => McpCaller.RunAsync<object>(async () =>
    {
        var (file, ownerId, access) = caller.File(file_id, Permissions.Edit);
        if (file.ProjectId is null) throw new McpException("图片不属于任何项目。");
        var project = store.State.Projects.First(x => x.Id == file.ProjectId);
        var segmentation = MaskFlowStore.NormalizeDataType(project.DataType) == "segmentation";
        var labels = store.GetProjectLabels(ownerId, file.ProjectId);
        var set = store.GetAnnotationSet(ownerId, file_id);
        var (width, height) = set is not null ? (set.Width, set.Height) : ImageTools.OrientedSize(await ReadAsync(file));
        var items = (set?.Annotations ?? []).ToList();

        string Label(string label) => MaskFlowStore.ResolveLabelClassId(label, labels) is var index and >= 0
            ? labels[index]
            : throw new McpException($"标签「{label}」不在项目中。可用标签：{string.Join("、", labels)}；需要新标签请先 update_labels。");

        var deleted = 0;
        foreach (var id in delete ?? [])
        {
            deleted += items.RemoveAll(x => x.Id == id) > 0 ? 1 : throw new McpException($"没有 id 为 {id} 的目标。");
        }

        foreach (var change in update ?? [])
        {
            var index = items.FindIndex(x => x.Id == change.Id);
            if (index < 0) throw new McpException($"没有 id 为 {change.Id} 的目标。");
            var item = items[index];
            var label = change.Label is null ? item.Label : Label(change.Label);
            var polygon = change.Polygon is null ? null : ParsePolygon(change.Polygon);
            var box = polygon is not null ? BoxOf(polygon) : change.Box is null ? item.Bbox : ParseBox(change.Box);
            // A moved box carries its polygon along (segmentation); detection targets just drop it.
            var segment = polygon ?? (change.Box is null ? item.Segment : segmentation && item.Segment is not null ? Refit(item.Segment, item.Bbox, box) : null);
            items[index] = item with
            {
                Label = label, ClassId = MaskFlowStore.ResolveLabelClassId(label, labels), Bbox = box, Segment = segment, Confirmed = false
            };
        }

        foreach (var target in add ?? [])
        {
            var label = Label(target.Label);
            var polygon = target.Polygon is null ? null : ParsePolygon(target.Polygon);
            var box = polygon is not null ? BoxOf(polygon) : target.Box is not null ? ParseBox(target.Box) : throw new McpException("新目标需要 box 或 polygon。");
            if (segmentation && polygon is null) throw new McpException("分割项目的新目标需要 polygon。");
            items.Add(new AnnotationItem("ann_" + Util.Id(), MaskFlowStore.ResolveLabelClassId(label, labels), label, box, polygon, 1.0, false));
        }

        var saved = await store.SaveAnnotationSetAsync(ownerId, new AnnotationSaveRequest(file_id, width, height, items), access?.Role ?? "owner");
        return new
        {
            fileId = file_id, added = add?.Length ?? 0, updated = update?.Length ?? 0, deleted,
            targets = saved.Annotations.Select((x, index) => Describe(x, index + 1))
        };
    });

    [McpServerTool(Name = "confirm_annotations", Title = "确认标注", Destructive = false, Idempotent = true)]
    [Description("Mark labelled targets as human-confirmed. Only call this after the user has agreed (e.g. 'confirm everything above 0.8'). Optional filters combine: file_ids, labels, min_confidence. Owner/reviewer only.")]
    public Task<object> ConfirmAnnotations(string project_id, int[]? file_ids = null, string[]? labels = null,
        [Description("Only confirm targets with AI confidence at least this (0-1)")] double min_confidence = 0) => McpCaller.RunAsync<object>(async () =>
    {
        var access = caller.Project(project_id, Permissions.Review);
        var (targets, images) = await store.ConfirmAnnotationsAsync(access.OwnerId, project_id, file_ids, labels, min_confidence);
        return new { confirmedTargets = targets, images };
    });

    [McpServerTool(Name = "review_image", Title = "提交 / 审核", Idempotent = false)]
    [Description("Team review workflow for one image: submit (annotator hands it in), approve (reviewer; confirms all labelled targets), reject (reviewer; comment required).")]
    public Task<object> ReviewImage(int file_id, [Description("submit | approve | reject")] string action, string? comment = null) =>
        McpCaller.RunAsync<object>(async () =>
        {
            if (action is not ("submit" or "approve" or "reject")) throw new McpException("action 只能是 submit、approve 或 reject。");
            var (_, ownerId, _) = caller.File(file_id, action == "submit" ? Permissions.Edit : Permissions.Review);
            var set = await store.ReviewAnnotationAsync(caller.UserId, ownerId, file_id, action, comment);
            return new { fileId = file_id, set.ReviewStatus, set.ReviewComment };
        });

    [McpServerTool(Name = "evaluate_model", Title = "评估模型", ReadOnly = true, Idempotent = true)]
    [Description("Evaluate a model against human-confirmed annotations: runs it on up to max_images fully confirmed images, matches predictions to labels (same class, IoU >= iou) and reports per-class precision/recall/F1 plus the images with the most mistakes (inspect with view_image and model_id). Uses 1 AI quota per image. Images used in training make the numbers optimistic; the training test split is the unbiased measure.")]
    public Task<object> EvaluateModel(string model_id, string project_id, int max_images = 20, double conf = 0.25, double iou = 0.5) =>
        McpCaller.RunAsync<object>(async () =>
        {
            var access = caller.Project(project_id, Permissions.Edit);
            var ownerId = access.OwnerId;
            if (annotator.FindModel(ownerId, model_id) is null) throw new McpException("模型不存在，先用 list_models 查看。");
            var sets = store.State.AnnotationSets.Where(x => x.UserId == ownerId).ToDictionary(x => x.FileId);
            var candidates = store.State.Files
                .Where(x => x.UserId == ownerId && x.ProjectId == project_id && x.Kind == "image" && sets.TryGetValue(x.Id, out var set)
                            && set.Annotations.Count > 0 && set.Annotations.All(a => a.Confirmed && MaskFlowStore.IsExportableAnnotation(a)))
                .OrderBy(x => x.Id)
                .Take(Math.Clamp(max_images, 1, 100))
                .ToList();
            if (candidates.Count == 0) throw new McpException("没有全部目标都已人工确认的图片，无法评估。先确认一些图片的标注。");
            store.EnsureAiQuotaAvailable(store.GetUser(ownerId)!, candidates.Count);

            var perImage = new List<(FileItem File, List<ClassMatch> Matches)>();
            foreach (var file in candidates)
            {
                var predictions = await PredictAsync(ownerId, file, AutoAnnotationOptions.Create("model", conf, null, model_id));
                perImage.Add((file, DetectionMatching.Match(sets[file.Id].Annotations, predictions, iou)));
            }

            var classes = DetectionMatching.Sum(perImage.SelectMany(x => x.Matches));
            var total = new ClassMatch("all", classes.Sum(x => x.TruePositives), classes.Sum(x => x.FalsePositives), classes.Sum(x => x.FalseNegatives));
            object Row(ClassMatch x) => new
            {
                label = x.Label, tp = x.TruePositives, fp = x.FalsePositives, fn = x.FalseNegatives,
                precision = Math.Round(x.Precision, 3), recall = Math.Round(x.Recall, 3), f1 = Math.Round(x.F1, 3)
            };
            return new
            {
                modelId = model_id,
                images = candidates.Count,
                iou,
                conf,
                overall = Row(total),
                perClass = classes.Select(Row),
                worstImages = perImage
                    .Select(x => new { fileId = x.File.Id, x.File.Name, falsePositives = x.Matches.Sum(m => m.FalsePositives), missed = x.Matches.Sum(m => m.FalseNegatives) })
                    .Where(x => x.falsePositives + x.missed > 0)
                    .OrderByDescending(x => x.falsePositives + x.missed)
                    .Take(10)
            };
        });

    async Task<List<AnnotationItem>> PredictAsync(int ownerId, FileItem file, AutoAnnotationOptions options)
    {
        store.EnsureAiQuotaAvailable(store.GetUser(ownerId)!);
        AnnotationSaveRequest result;
        await using (await samGate.WaitAsync(caller.Aborted))
        {
            result = await annotator.PredictAsync(ownerId, file, options, caller.Aborted);
        }

        await store.ConsumeAiQuotaAsync(ownerId, 1);
        return result.Annotations;
    }

    async Task<byte[]> ReadAsync(FileItem file)
    {
        await using var stream = await store.OpenStoredObjectAsync(file.Path);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, caller.Aborted);
        return buffer.ToArray();
    }

    static object Describe(AnnotationItem x, int number) => new
    {
        number,
        x.Id,
        label = x.Label,
        box = BoxArray(x.Bbox),
        polygonPoints = x.Segment is null ? 0 : x.Segment.Count / 2,
        confidence = Math.Round(x.Confidence, 3),
        x.Confirmed
    };

    static string Legend(AnnotationItem x, int number, bool prediction = false)
    {
        var box = string.Join(", ", BoxArray(x.Bbox).Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        var state = prediction ? $"置信度 {x.Confidence:0.00}" : x.Confirmed ? "已确认" : $"未确认，AI 置信度 {x.Confidence:0.00}";
        return $"#{number} {x.Label ?? "（未分配标签）"}  [{box}]  {state}{(prediction ? "" : $"  id={x.Id}")}";
    }

    static double[] BoxArray(YoloBox box) =>
    [
        Math.Round(box.Cx - box.Width / 2, 4), Math.Round(box.Cy - box.Height / 2, 4),
        Math.Round(box.Cx + box.Width / 2, 4), Math.Round(box.Cy + box.Height / 2, 4)
    ];

    public static YoloBox ParseBox(double[] box)
    {
        if (box.Length != 4 || box.Any(v => !double.IsFinite(v))) throw new McpException("box 需要 4 个数 [x1, y1, x2, y2]。");
        var (x1, y1, x2, y2) = (Math.Clamp(box[0], 0, 1), Math.Clamp(box[1], 0, 1), Math.Clamp(box[2], 0, 1), Math.Clamp(box[3], 0, 1));
        if (x2 <= x1 || y2 <= y1) throw new McpException("box 需要 x2 > x1 且 y2 > y1（归一化 0-1 坐标）。");
        return new YoloBox((x1 + x2) / 2, (y1 + y2) / 2, x2 - x1, y2 - y1);
    }

    public static List<double> ParsePolygon(double[] polygon)
    {
        if (polygon.Length < 6 || polygon.Length % 2 != 0 || polygon.Any(v => !double.IsFinite(v)))
            throw new McpException("polygon 需要至少 3 个点 [x1, y1, x2, y2, x3, y3, ...]。");
        return polygon.Select(v => Math.Clamp(v, 0, 1)).ToList();
    }

    public static YoloBox BoxOf(List<double> polygon)
    {
        var xs = polygon.Where((_, i) => i % 2 == 0).ToList();
        var ys = polygon.Where((_, i) => i % 2 == 1).ToList();
        return new YoloBox((xs.Min() + xs.Max()) / 2, (ys.Min() + ys.Max()) / 2, xs.Max() - xs.Min(), ys.Max() - ys.Min());
    }

    // Maps a polygon from its old box onto a new one (move + scale), as the web editor does.
    public static List<double> Refit(List<double> polygon, YoloBox from, YoloBox to)
    {
        double MapX(double x) => from.Width <= 0 ? to.Cx : to.Cx + (x - from.Cx) * to.Width / from.Width;
        double MapY(double y) => from.Height <= 0 ? to.Cy : to.Cy + (y - from.Cy) * to.Height / from.Height;
        return polygon.Select((v, i) => Math.Clamp(i % 2 == 0 ? MapX(v) : MapY(v), 0, 1)).ToList();
    }
}
