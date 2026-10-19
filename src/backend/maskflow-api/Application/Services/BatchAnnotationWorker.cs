using MaskFlow.Api.Infrastructure.Security;

// Runs "batch auto annotation" tasks on the server, one image at a time, so closing the browser
// does not stop the job. Progress lives in the task record; restarts resume where they left off.
public sealed class BatchAnnotationWorker(
    MaskFlowStore store,
    AutoAnnotationService annotator,
    SamInferenceGate samGate,
    AccountMailer mailer,
    ILogger<BatchAnnotationWorker> logger) : BackgroundService
{
    readonly SemaphoreSlim wake = new(0);

    public void Wake()
    {
        if (wake.CurrentCount == 0) wake.Release();
    }

    // Validates and queues a batch for the project; scope "unannotated" (default) or "all".
    public async Task<TaskItem> QueueAsync(ProjectAccess access, AnnotationBatchRequest request)
    {
        var ownerId = access.OwnerId;
        store.EnsureAiQuotaAvailable(store.GetUser(ownerId)!);
        var options = AutoAnnotationOptions.Create(request.Mode, request.Conf, request.DefaultLabel, request.ModelId);
        var files = store.State.Files
            .Where(x => x.UserId == ownerId && x.ProjectId == access.Project.Id && x.Kind == "image")
            .OrderBy(x => x.Id)
            .ToList();
        if (files.Count == 0) throw new BadHttpRequestException("项目中还没有图片。", 400);
        annotator.Validate(ownerId, files[0], options);

        List<int> fileIds;
        if (request.FileIds is { Count: > 0 })
        {
            var inProject = files.Select(x => x.Id).ToHashSet();
            fileIds = request.FileIds.Distinct().Where(inProject.Contains).ToList();
            if (fileIds.Count != request.FileIds.Distinct().Count()) throw new BadHttpRequestException("所选图片中有不属于该项目或已删除的图片，请刷新后重试。", 400);
        }
        else
        {
            var annotated = store.State.AnnotationSets.Where(x => x.UserId == ownerId && MaskFlowStore.IsAnnotated(x)).Select(x => x.FileId).ToHashSet();
            fileIds = (request.Scope == "all" ? files : files.Where(x => !annotated.Contains(x.Id))).Select(x => x.Id).ToList();
            if (fileIds.Count == 0) throw new BadHttpRequestException("没有需要处理的图片：所有图片都已有标注。", 400);
        }

        var active = store.State.Tasks.Any(x => x.UserId == ownerId && x.ProjectId == access.Project.Id
            && x.Type == MaskFlowStore.BatchAnnotationTaskType && x.Status is "queued" or "running");
        if (active) throw new BadHttpRequestException("该项目已有正在进行的批量标注任务。", 409);

        var task = await store.CreateBatchAnnotationTaskAsync(ownerId, request with { Mode = options.Mode }, fileIds);
        Wake();
        return task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunNextAsync(stoppingToken))
                {
                    await wake.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Batch annotation loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    // Processes the oldest queued batch task. Returns false when there was nothing to do.
    public async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        var task = store.State.Tasks
            .Where(x => x.Type == MaskFlowStore.BatchAnnotationTaskType && x.Status == "queued")
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefault();
        if (task is null || !await store.TryStartBatchTaskAsync(task.Id)) return task is not null;

        var input = store.GetBatchInput(task);
        if (input is null)
        {
            await store.RecordBatchProgressAsync(task.Id, BatchProgress.Empty with { LastError = "任务参数缺失" }, 0, "failed");
            return true;
        }

        var options = AutoAnnotationOptions.Create(input.Mode, input.Conf, input.DefaultLabel, input.ModelId);
        var progress = BatchProgress.From(task.Result);
        var total = input.FileIds.Count;
        for (var index = progress.Processed; index < total; index++)
        {
            if (store.State.Tasks.FirstOrDefault(x => x.Id == task.Id)?.Status != "running") return true;

            var user = store.GetUser(task.UserId);
            var file = store.State.Files.FirstOrDefault(x => x.Id == input.FileIds[index] && x.UserId == task.UserId);
            string? error = null;
            var stop = false;
            var detected = -1;
            if (user is null)
            {
                error = "用户不存在";
                stop = true;
            }
            else if (file is null)
            {
                error = $"图片 {input.FileIds[index]} 已被删除";
            }
            else
            {
                try
                {
                    store.EnsureAiQuotaAvailable(user);
                    await using var slot = await samGate.WaitAsync(cancellationToken);
                    detected = (await annotator.RunAsync(user.Id, file, options, recordTask: false, cancellationToken)).Detected;
                }
                catch (BadHttpRequestException ex)
                {
                    error = $"{file.Name}: {ex.Message}";
                    // Quota, labels, project or model problems affect every remaining image; a file
                    // deleted mid-run does not.
                    stop = ex.Message != "File not found.";
                }
                catch (SamServiceException ex)
                {
                    error = $"{file.Name}: {ex.Detail}";
                }
                catch (HttpRequestException ex)
                {
                    error = $"推理服务不可用：{ex.Message}";
                    stop = true;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    error = $"{file.Name}: 推理超时";
                }
            }

            progress = error is not null
                ? progress with { Processed = progress.Processed + 1, Failed = progress.Failed + 1, LastError = error }
                : detected == 0
                    ? progress with { Processed = progress.Processed + 1, NoTargets = progress.NoTargets + 1 }
                    : progress with { Processed = progress.Processed + 1, Succeeded = progress.Succeeded + 1 };
            var finalStatus = stop ? "failed" : null;
            var status = await store.RecordBatchProgressAsync(task.Id, progress, total, finalStatus);
            if (status == "failed") await NotifyAsync(task, status, progress);
            if (status != "running") return true;
        }

        // Nothing labelled at all is a failed run, whether every image errored or nothing was detected.
        var outcome = progress.Succeeded == 0 && progress.Processed > 0 ? "failed" : "completed";
        if (progress.Succeeded == 0 && progress.NoTargets > 0 && progress.Failed == 0)
        {
            progress = progress with { LastError = "所有图片都没有检测到目标：可降低置信度阈值，或检查模型类别名是否与项目标签一致。" };
        }
        await store.RecordBatchProgressAsync(task.Id, progress, total, outcome);
        await NotifyAsync(task, outcome, progress);
        return true;
    }

    // Honours the existing "任务邮件通知" account setting (on by default).
    async Task NotifyAsync(TaskItem task, string status, BatchProgress progress)
    {
        var user = store.GetUser(task.UserId);
        if (user is null) return;
        var settings = store.State.NotificationSettings.GetValueOrDefault(user.Id.ToString(), NotificationSettings.Default());
        if (!settings.EmailTask) return;
        var projectName = store.State.Projects.FirstOrDefault(x => x.Id == task.ProjectId)?.Name ?? "未命名项目";
        await mailer.SendBatchFinishedAsync(user.Email, projectName, status, progress);
    }
}
