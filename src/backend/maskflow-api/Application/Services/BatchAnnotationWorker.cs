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
                    await annotator.RunAsync(user.Id, file, options, recordTask: false, cancellationToken);
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

            progress = error is null
                ? progress with { Processed = progress.Processed + 1, Succeeded = progress.Succeeded + 1 }
                : progress with { Processed = progress.Processed + 1, Failed = progress.Failed + 1, LastError = error };
            var finalStatus = stop ? "failed" : null;
            var status = await store.RecordBatchProgressAsync(task.Id, progress, total, finalStatus);
            if (status == "failed") await NotifyAsync(task, status, progress);
            if (status != "running") return true;
        }

        var outcome = progress.Succeeded == 0 && progress.Failed > 0 ? "failed" : "completed";
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
