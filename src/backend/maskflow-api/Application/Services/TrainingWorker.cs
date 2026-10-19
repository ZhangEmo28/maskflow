using System.Globalization;
using System.Net;
using System.Text.Json;

// Drives training runs on the trainer service: builds the dataset from the project, uploads it,
// follows progress, then keeps the weights/plots/metrics and registers the ONNX model.
// The trainer runs one job at a time; queued runs wait here in creation order.
public sealed class TrainingWorker(
    MaskFlowStore store,
    IHttpClientFactory clientFactory,
    AccountMailer mailer,
    ILogger<TrainingWorker> logger) : BackgroundService
{
    readonly SemaphoreSlim wake = new(0);
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    HttpClient Client => clientFactory.CreateClient("trainer");

    public void Wake()
    {
        if (wake.CurrentCount == 0) wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.RequeueInterruptedTrainingsAsync();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await wake.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Training loop failed");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    // Polls every run the trainer owns, then starts the oldest queued run once the trainer is free.
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var run in store.State.TrainingRuns.Where(x => MaskFlowStore.RemoteTrainingStatuses.Contains(x.Status)).ToList())
        {
            await PollAsync(run, cancellationToken);
        }

        if (store.State.TrainingRuns.Any(x => MaskFlowStore.RemoteTrainingStatuses.Contains(x.Status) || x.Status == "preparing")) return;
        var next = store.State.TrainingRuns.Where(x => x.Status == "queued").OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (next is not null) await StartAsync(next, cancellationToken);
    }

    public async Task<IReadOnlyList<TrainerWeight>> ListWeightsAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync("/api/weights", cancellationToken);
        using var document = JsonDocument.Parse(await ReadSuccessAsync(response, cancellationToken));
        return document.RootElement.GetProperty("weights").EnumerateArray()
            .Select(x => new TrainerWeight(x.GetProperty("name").GetString()!, x.GetProperty("size").GetInt64(), x.GetProperty("task").GetString()!))
            .ToList();
    }

    public async Task<TrainingRun?> CancelAsync(string runId, CancellationToken cancellationToken)
    {
        var before = store.GetTrainingRun(runId);
        var cancelled = await store.FinishTrainingRunAsync(runId, "cancelled", null);
        if (cancelled is not null && before?.Status != "queued") await StopRemoteAsync(runId, cancellationToken);
        return cancelled;
    }

    async Task StartAsync(TrainingRun run, CancellationToken cancellationToken)
    {
        try
        {
            // A run cancelled while the trainer was unreachable may still be running there; stop it first.
            using (var status = await Client.GetAsync("/api/status", cancellationToken))
            {
                using var document = JsonDocument.Parse(await ReadSuccessAsync(status, cancellationToken));
                if (document.RootElement.GetProperty("running").GetString() is { } running)
                {
                    var known = store.GetTrainingRun(running);
                    if (known is null || !MaskFlowStore.IsActiveTraining(known)) await StopRemoteAsync(running, cancellationToken);
                    return;
                }
            }
        }
        catch (Exception ex) when (IsServiceError(ex, cancellationToken))
        {
            await store.UpdateTrainingRunAsync(run.Id, x => x.Status == "queued" && x.Error != Describe(ex) ? x with { Error = Describe(ex) } : null);
            return;
        }

        var started = await store.UpdateTrainingRunAsync(run.Id, x => x.Status == "queued"
            ? x with { Status = "preparing", StartedAt = DateTimeOffset.UtcNow, Error = null }
            : null);
        if (started is null) return;

        string? zip = null;
        try
        {
            (zip, var images) = await store.WriteTrainingDatasetAsync(started);
            using var content = new MultipartFormDataContent();
            await using var stream = File.OpenRead(zip);
            content.Add(new StreamContent(stream), "dataset", "dataset.zip");
            void Field(string name, object value) => content.Add(new StringContent(Convert.ToString(value, CultureInfo.InvariantCulture)!), name);
            Field("run_id", started.Id);
            Field("base_weight", started.Request.BaseWeight);
            Field("epochs", started.Request.Epochs);
            Field("imgsz", started.Request.ImageSize);
            Field("batch", started.Request.Batch);
            Field("patience", started.Request.Patience);
            using var response = await Client.PostAsync("/api/train", content, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                await store.UpdateTrainingRunAsync(run.Id, x => x.Status == "preparing" ? x with { Status = "queued", StartedAt = null, Error = "训练服务正忙，稍后自动重试。" } : null);
                return;
            }

            await ReadSuccessAsync(response, cancellationToken);
            var moved = await store.UpdateTrainingRunAsync(run.Id, x => x.Status == "preparing" ? x with { Status = "training", ImageCount = images } : null);
            // Cancelled or deleted while the dataset was uploading.
            if (moved is null) await StopRemoteAsync(run.Id, cancellationToken);
        }
        catch (Exception ex) when (IsServiceError(ex, cancellationToken))
        {
            var failed = await store.FinishTrainingRunAsync(run.Id, "failed", Describe(ex));
            if (failed is not null) await NotifyAsync(failed);
        }
        finally
        {
            if (zip is not null) File.Delete(zip);
        }
    }

    async Task PollAsync(TrainingRun run, CancellationToken cancellationToken)
    {
        JsonElement status;
        try
        {
            using var response = await Client.GetAsync($"/api/train/{run.Id}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var lost = await store.FinishTrainingRunAsync(run.Id, "failed", "训练服务中找不到这次训练（服务可能重启过或数据被清理），请重新开始训练。");
                if (lost is not null) await NotifyAsync(lost);
                return;
            }

            using var document = JsonDocument.Parse(await ReadSuccessAsync(response, cancellationToken));
            status = document.RootElement.Clone();
        }
        catch (Exception ex) when (IsServiceError(ex, cancellationToken))
        {
            // Transient: keep the run and show why its progress is stale.
            var message = Describe(ex);
            await store.UpdateTrainingRunAsync(run.Id, x => MaskFlowStore.RemoteTrainingStatuses.Contains(x.Status) && x.Error != message ? x with { Error = message } : null);
            return;
        }

        var phase = status.GetProperty("phase").GetString();
        var history = ParseHistory(status);
        switch (phase)
        {
            case "completed":
                await CollectAsync(run, status, history, cancellationToken);
                break;
            case "failed" or "cancelled":
                var error = status.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null;
                var finished = await store.FinishTrainingRunAsync(run.Id, phase, phase == "failed" ? error ?? "训练失败。" : null, history);
                await DeleteRemoteAsync(run.Id, cancellationToken);
                if (finished is not null) await NotifyAsync(finished);
                break;
            default:
                var next = phase is "evaluating" or "exporting" ? phase : "training";
                await store.UpdateTrainingRunAsync(run.Id, x =>
                    MaskFlowStore.RemoteTrainingStatuses.Contains(x.Status) && (x.Status != next || x.Epoch != history.Count || x.Error is not null)
                        ? x with { Status = next, Epoch = history.Count, History = history, Error = null }
                        : null);
                break;
        }
    }

    async Task CollectAsync(TrainingRun run, JsonElement status, List<Dictionary<string, double>> history, CancellationToken cancellationToken)
    {
        var available = status.GetProperty("artifacts").EnumerateArray().Select(x => x.GetString()).ToHashSet();
        var metrics = status.TryGetProperty("metrics", out var value) && value.ValueKind == JsonValueKind.Object ? value.Clone() : (JsonElement?)null;
        var artifacts = new List<TrainingArtifact>();
        var temp = Directory.CreateTempSubdirectory("maskflow-train-");
        TrainingRun? completed;
        try
        {
            foreach (var name in MaskFlowStore.TrainingArtifactNames.Where(available.Contains))
            {
                var local = Path.Combine(temp.FullName, name);
                using (var response = await Client.GetAsync($"/api/train/{run.Id}/artifacts/{name}", HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode) await ReadSuccessAsync(response, cancellationToken);
                    await using var file = File.Create(local);
                    await response.Content.CopyToAsync(file, cancellationToken);
                }

                artifacts.Add(new TrainingArtifact(name, await store.PersistTrainingArtifactAsync(run, name, local), new FileInfo(local).Length));
            }

            completed = await store.CompleteTrainingRunAsync(run.Id, artifacts, metrics, history);
            var onnx = Path.Combine(temp.FullName, "best.onnx");
            // Classification models can't drive box/mask auto-annotation, so only detect/segment are registered.
            if (completed is { Task: "detect" or "segment" } && File.Exists(onnx))
            {
                try
                {
                    var model = await store.AddTrainedModelAsync(completed, onnx, ClassNames(metrics));
                    completed = await store.UpdateTrainingRunAsync(run.Id, x => x with { ModelId = model.Id }) ?? completed;
                }
                catch (BadHttpRequestException ex)
                {
                    completed = await store.UpdateTrainingRunAsync(run.Id, x => x with { Error = $"训练已完成，但模型没有自动注册：{ex.Message}" }) ?? completed;
                }
            }
        }
        catch (BadHttpRequestException ex)
        {
            await store.DeleteObjectsQuietlyAsync(artifacts.Select(x => x.Path));
            completed = await store.FinishTrainingRunAsync(run.Id, "failed",
                ex.StatusCode == 403 ? "存储空间不足，训练结果没有保存。请清理空间或升级套餐后重新训练。" : ex.Message, history);
        }
        catch (Exception ex) when (IsServiceError(ex, cancellationToken))
        {
            // Try again on the next poll; the trainer keeps the run until it is collected.
            await store.DeleteObjectsQuietlyAsync(artifacts.Select(x => x.Path));
            var message = $"正在取回训练结果：{Describe(ex)}";
            await store.UpdateTrainingRunAsync(run.Id, x => MaskFlowStore.IsActiveTraining(x) ? x with { Error = message } : null);
            return;
        }
        finally
        {
            temp.Delete(recursive: true);
        }

        await DeleteRemoteAsync(run.Id, cancellationToken);
        if (completed is not null) await NotifyAsync(completed);
    }

    static List<string> ClassNames(JsonElement? metrics) =>
        metrics is { } value && value.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(x => x.ToString()).ToList()
            : [];

    public static List<Dictionary<string, double>> ParseHistory(JsonElement status) =>
        status.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array
            ? history.EnumerateArray()
                .Select(row => row.EnumerateObject()
                    .Where(x => x.Value.ValueKind == JsonValueKind.Number)
                    .ToDictionary(x => x.Name, x => x.Value.GetDouble()))
                .ToList()
            : [];

    async Task StopRemoteAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            using (await Client.PostAsync($"/api/train/{runId}/cancel", null, cancellationToken)) { }
            await DeleteRemoteAsync(runId, cancellationToken);
        }
        catch (Exception ex) when (IsServiceError(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Could not stop training run {RunId} on the trainer", runId);
        }
    }

    async Task DeleteRemoteAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            using (await Client.DeleteAsync($"/api/train/{runId}", cancellationToken)) { }
        }
        catch (Exception ex) when (IsServiceError(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Could not delete training run {RunId} on the trainer", runId);
        }
    }

    static async Task<string> ReadSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new SamServiceException((int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
        return body;
    }

    static bool IsServiceError(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or SamServiceException or BadHttpRequestException or JsonException or IOException or KeyNotFoundException or InvalidOperationException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    public static string Describe(Exception ex) => ex switch
    {
        SamServiceException sam => sam.Detail,
        HttpRequestException http => $"训练服务不可用：{http.Message}",
        TaskCanceledException => "训练服务超时",
        _ => ex.Message
    };

    // Honours the "任务邮件通知" setting of whoever started the run.
    async Task NotifyAsync(TrainingRun run)
    {
        var user = store.GetUser(run.CreatedBy);
        if (user is null) return;
        var settings = store.State.NotificationSettings.GetValueOrDefault(user.Id.ToString(), NotificationSettings.Default());
        if (!settings.EmailTask) return;
        var projectName = store.State.Projects.FirstOrDefault(x => x.Id == run.ProjectId)?.Name ?? "未命名项目";
        await mailer.SendTrainingFinishedAsync(user.Email, projectName, run);
    }
}
