using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class ProjectTools(McpCaller caller, MaskFlowStore store, ProjectService projects, TrainingWorker trainer)
{
    [McpServerTool(Name = "list_projects", Title = "列出项目", ReadOnly = true, Idempotent = true)]
    [Description("List the projects the user can access (own and shared), with their labels, image and annotation counts and the user's role.")]
    public object ListProjects() => McpCaller.Run(() => new
    {
        projects = projects.List(caller.UserId).Select(x => new
        {
            x.Id, x.Name, x.Description, x.DataType, x.Role, x.ImageCount, x.AnnotationCount, x.Labels, x.UpdatedAt
        })
    });

    [McpServerTool(Name = "get_project", Title = "项目概况", ReadOnly = true, Idempotent = true)]
    [Description("Overview of one project: labels and their SAM text prompts, annotation progress, review status, AI quota, models, running tasks and recent trainings.")]
    public object GetProject([Description("Project id from list_projects")] string project_id) => McpCaller.Run(() =>
    {
        var access = caller.Project(project_id, Permissions.Read);
        var ownerId = access.OwnerId;
        var labels = store.GetProjectLabels(ownerId, project_id);
        var files = store.State.Files.Where(x => x.UserId == ownerId && x.ProjectId == project_id && x.Kind == "image").Select(x => x.Id).ToHashSet();
        var sets = store.State.AnnotationSets.Where(x => x.UserId == ownerId && files.Contains(x.FileId)).ToList();
        var targets = sets.SelectMany(x => x.Annotations).ToList();
        var owner = store.GetUser(ownerId)!;
        return new
        {
            project = new { access.Project.Id, access.Project.Name, access.Project.Description, access.Project.DataType, role = access.Role },
            labels,
            labelPrompts = store.GetLabelPrompts(ownerId, project_id),
            images = files.Count,
            annotatedImages = sets.Count,
            targets = targets.Count,
            confirmedTargets = targets.Count(x => x.Confirmed),
            unlabeledTargets = targets.Count(x => !MaskFlowStore.IsExportableAnnotation(x)),
            reviewStatus = sets.GroupBy(x => x.ReviewStatus).ToDictionary(x => x.Key, x => x.Count()),
            aiQuota = store.GetQuota(owner),
            models = store.State.AiModels.Where(x => x.UserId == ownerId && (x.ProjectId is null || x.ProjectId == project_id))
                .Select(x => new { x.Id, x.Name, x.ClassNames, x.CreatedAt }),
            runningTasks = store.State.Tasks.Where(x => x.ProjectId == project_id && x.Status is "queued" or "running")
                .Select(x => new { x.Id, x.Type, x.Title, x.Status, x.Progress }),
            trainings = store.State.TrainingRuns.Where(x => x.ProjectId == project_id).OrderByDescending(x => x.CreatedAt).Take(5)
                .Select(x => new { x.Id, x.Name, x.Status, baseWeight = x.Request.BaseWeight, x.Epoch, epochs = x.Request.Epochs, metrics = MaskFlowStore.TrainingHeadline(x) }),
            webUrl = $"{McpCaller.PublicUrl}/annotate.html"
        };
    });

    [McpServerTool(Name = "create_project", Title = "创建项目", Idempotent = false)]
    [Description("Create a new project owned by the user. data_type is 'detection' (boxes) or 'segmentation' (polygons). Images are uploaded by the user in the web app afterwards.")]
    public Task<object> CreateProject(
        [Description("Project name")] string name,
        [Description("Class names, e.g. [\"car\", \"person\"]; Chinese names are fine")] string[] labels,
        [Description("'detection' or 'segmentation'")] string data_type = "detection",
        [Description("Optional description")] string? description = null) => McpCaller.RunAsync<object>(async () =>
    {
        if (string.IsNullOrWhiteSpace(name)) throw new McpException("项目名称不能为空。");
        var project = await projects.CreateAsync(caller.UserId, new ProjectCreate(name.Trim(), description, data_type, null));
        var saved = await store.SaveProjectLabelsAsync(caller.UserId, project.Id, labels);
        return new { project = project with { Labels = saved }, uploadUrl = $"{McpCaller.PublicUrl}/files.html" };
    });

    [McpServerTool(Name = "update_labels", Title = "添加标签 / 设置提示词", Idempotent = true)]
    [Description("Add labels to a project and/or set the English text prompt SAM 3 uses to find each label (concepts mode). Existing labels are kept; to rename or delete labels use the web app.")]
    public Task<object> UpdateLabels(
        string project_id,
        [Description("New labels to append")] string[]? add_labels = null,
        [Description("label -> English prompt, e.g. {\"安全帽\": \"hard hat\"}; an empty string removes a prompt")] Dictionary<string, string>? prompts = null) =>
        McpCaller.RunAsync<object>(async () =>
        {
            var access = caller.Project(project_id, Permissions.Review);
            var labels = store.GetProjectLabels(access.OwnerId, project_id);
            if (add_labels is { Length: > 0 })
            {
                labels = await store.SaveProjectLabelsAsync(access.OwnerId, project_id, labels.Concat(add_labels));
            }

            var current = store.GetLabelPrompts(access.OwnerId, project_id);
            if (prompts is { Count: > 0 })
            {
                var unknown = prompts.Keys.Where(x => MaskFlowStore.ResolveLabelClassId(x, labels) < 0).ToList();
                if (unknown.Count > 0) throw new McpException($"这些标签不在项目中：{string.Join("、", unknown)}。当前标签：{string.Join("、", labels)}");
                var merged = new Dictionary<string, string?>(current.ToDictionary(x => x.Key, x => (string?)x.Value), StringComparer.OrdinalIgnoreCase);
                foreach (var (label, prompt) in prompts) merged[label] = prompt;
                current = await store.SaveLabelPromptsAsync(access.OwnerId, project_id, merged);
            }

            return new { labels, prompts = current };
        });

    [McpServerTool(Name = "check_quality", Title = "检查标注质量", ReadOnly = true, Idempotent = true)]
    [Description("Rule-based quality check of a project's annotations: unlabeled/invalid/tiny/huge boxes, duplicates, conflicting labels on the same object, low-confidence unconfirmed AI targets, missing polygons, rejected reviews, sparse or imbalanced classes. Returns counts and the flagged images/targets; use view_image to inspect them.")]
    public object CheckQuality(string project_id, [Description("Maximum issues to list (default 80)")] int max_issues = 80) => McpCaller.Run(() =>
    {
        var access = caller.Project(project_id, Permissions.Read);
        return QualityCheck.Run(store.State, access.Project, store.GetProjectLabels(access.OwnerId, project_id), Math.Clamp(max_issues, 1, 500));
    });

    [McpServerTool(Name = "export_dataset", Title = "导出数据集", Idempotent = false)]
    [Description("Build a dataset archive from the project (formats: yolo, yolo-detect, yolo-segment, classification-crops, coco, voc). Splits are stratified by label with near-duplicates kept together. The user downloads it from the web app.")]
    public Task<object> ExportDataset(
        string project_id,
        string format = "yolo",
        [Description("Percent of images for train/val/test; must sum to 100")] int train = 70, int val = 20, int test = 10,
        [Description("Only include human-confirmed targets")] bool confirmed_only = false) => McpCaller.RunAsync<object>(async () =>
    {
        var access = caller.Project(project_id, Permissions.Export);
        var export = await store.CreateDatasetExportAsync(access.OwnerId, new ExportRequest(project_id, null, new SplitConfig(train, val, test), format, ConfirmedOnly: confirmed_only));
        return new { export.Id, export.Status, export.Size, format = export.Config.Format, downloadPage = $"{McpCaller.PublicUrl}/export.html" };
    });

    [McpServerTool(Name = "list_models", Title = "列出模型", ReadOnly = true, Idempotent = true)]
    [Description("ONNX models usable for auto annotation in this project (uploaded or produced by training), with their class names.")]
    public object ListModels(string project_id) => McpCaller.Run(() =>
    {
        var access = caller.Project(project_id, Permissions.Read);
        var trained = store.State.TrainingRuns.Where(x => x.ModelId is not null).ToDictionary(x => x.ModelId!, x => x);
        return new
        {
            models = store.State.AiModels.Where(x => x.UserId == access.OwnerId && (x.ProjectId is null || x.ProjectId == project_id))
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new
                {
                    x.Id, x.Name, x.ClassNames, x.Size, x.CreatedAt,
                    trainingId = trained.GetValueOrDefault(x.Id)?.Id,
                    trainingMetrics = trained.TryGetValue(x.Id, out var run) ? MaskFlowStore.TrainingHeadline(run) : null
                })
        };
    });

    [McpServerTool(Name = "list_base_weights", Title = "列出基础权重", ReadOnly = true, Idempotent = true)]
    [Description("Pre-downloaded Ultralytics base weights available for training, with their task (detect/segment/classify). detect works for any project, segment needs a segmentation project, classify trains on box crops.")]
    public Task<object> ListBaseWeights() => McpCaller.RunAsync<object>(async () =>
    {
        _ = caller.User;
        try
        {
            return new { available = true, weights = await trainer.ListWeightsAsync(caller.Aborted) };
        }
        catch (Exception ex) when (ex is HttpRequestException or SamServiceException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new { available = false, error = TrainingWorker.Describe(ex), hint = "管理员需要启动训练服务并把 .pt 权重放到 资源/训练权重 目录。" };
        }
    });
}
