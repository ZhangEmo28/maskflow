using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using MaskFlow.Api.Infrastructure.Security;

// Background jobs for videos: extract frames via the inference service, and propagate labelled
// boxes from one frame to the next by prompting SAM with the previous boxes.
public sealed class VideoTaskWorker(
    MaskFlowStore store,
    IHttpClientFactory clientFactory,
    SamInferenceGate samGate,
    ILogger<VideoTaskWorker> logger) : BackgroundService
{
    readonly SemaphoreSlim wake = new(0);
    static readonly string[] TaskTypes = [MaskFlowStore.VideoFramesTaskType, MaskFlowStore.VideoPropagationTaskType];

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
                logger.LogError(ex, "Video task loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        var task = store.State.Tasks
            .Where(x => TaskTypes.Contains(x.Type) && x.Status == "queued")
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefault();
        if (task is null || !await store.TryStartBatchTaskAsync(task.Id)) return task is not null;

        var input = store.GetVideoTaskInput(task);
        if (input is null)
        {
            await store.RecordBatchProgressAsync(task.Id, BatchProgress.Empty with { LastError = "任务参数缺失" }, 0, "failed");
            return true;
        }

        try
        {
            if (task.Type == MaskFlowStore.VideoFramesTaskType) await ExtractFramesAsync(task, input, cancellationToken);
            else await PropagateAsync(task, input, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or SamServiceException or BadHttpRequestException or InvalidDataException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var message = ex switch
            {
                SamServiceException sam => sam.Detail,
                HttpRequestException http => $"推理服务不可用：{http.Message}",
                TaskCanceledException => "推理服务超时",
                _ => ex.Message
            };
            if (task.Type == MaskFlowStore.VideoFramesTaskType) await store.SetVideoStatusAsync(input.VideoId, "failed", message);
            await store.RecordBatchProgressAsync(task.Id, BatchProgress.Empty with { LastError = message }, 0, "failed");
        }

        return true;
    }

    async Task ExtractFramesAsync(TaskItem task, VideoTaskInput input, CancellationToken cancellationToken)
    {
        var video = store.State.Videos.FirstOrDefault(x => x.Id == input.VideoId) ?? throw new BadHttpRequestException("视频已被删除。", 404);
        await store.SetVideoStatusAsync(video.Id, "processing");

        using var content = new MultipartFormDataContent();
        await using var videoStream = await store.OpenStoredObjectAsync(video.Path);
        content.Add(new StreamContent(videoStream), "video", video.Name);
        content.Add(new StringContent(input.EverySeconds.ToString(CultureInfo.InvariantCulture)), "every_seconds");
        content.Add(new StringContent(input.MaxFrames.ToString(CultureInfo.InvariantCulture)), "max_frames");
        using var response = await clientFactory.CreateClient("sam").PostAsync("/api/video/frames", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SamServiceException((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken), response.Content.Headers.ContentType?.ToString());
        }

        using var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")?.Open() ?? throw new InvalidDataException("推理服务返回的帧缺少 manifest。"));
        var root = manifest.RootElement;

        var frames = new List<(int Index, string Path, long Size, string Phash)>();
        try
        {
            foreach (var frame in root.GetProperty("frames").EnumerateArray())
            {
                var entry = archive.GetEntry(frame.GetProperty("file").GetString()!) ?? throw new InvalidDataException("帧文件缺失。");
                using var bytes = new MemoryStream();
                await using (var stream = entry.Open()) await stream.CopyToAsync(bytes, cancellationToken);
                var data = bytes.ToArray();
                var index = frame.GetProperty("index").GetInt32();
                var path = await store.PersistFrameAsync(data, video.UserId, video.ProjectId, $"{Path.GetFileNameWithoutExtension(video.Name)}_f{index:000000}.jpg");
                frames.Add((index, path, data.Length, ImageTools.PerceptualHashHex(data)));
            }

            await store.CommitVideoFramesAsync(video.Id, frames, root.GetProperty("fps").GetDouble(), root.GetProperty("duration").GetDouble());
        }
        catch
        {
            await store.DeleteObjectsQuietlyAsync(frames.Select(x => x.Path));
            throw;
        }

        await store.RecordBatchProgressAsync(task.Id, new BatchProgress(frames.Count, frames.Count, 0, null), frames.Count, "completed");
    }

    async Task PropagateAsync(TaskItem task, VideoTaskInput input, CancellationToken cancellationToken)
    {
        var video = store.State.Videos.FirstOrDefault(x => x.Id == input.VideoId) ?? throw new BadHttpRequestException("视频已被删除。", 404);
        var start = video.FrameFileIds.IndexOf(input.FromFileId);
        if (start < 0) throw new BadHttpRequestException("起始帧不属于这个视频。", 400);
        var targets = video.FrameFileIds.Skip(start + 1).Take(input.Count).ToList();
        var previous = store.GetAnnotationSet(video.UserId, input.FromFileId)?.Annotations
            .Where(MaskFlowStore.IsExportableAnnotation)
            .ToList() ?? [];
        if (previous.Count == 0) throw new BadHttpRequestException("起始帧没有已分配类别的目标，无法传播。", 400);

        var progress = BatchProgress.Empty;
        foreach (var fileId in targets)
        {
            if (store.State.Tasks.FirstOrDefault(x => x.Id == task.Id)?.Status != "running") return;
            var owner = store.GetUser(video.UserId);
            if (owner is null) break;
            var file = store.State.Files.FirstOrDefault(x => x.Id == fileId);
            if (file is null)
            {
                // A deleted frame is skipped; the next frame is prompted with the same boxes.
                progress = progress with { Processed = progress.Processed + 1 };
                continue;
            }

            store.EnsureAiQuotaAvailable(owner);

            string body;
            await using (var slot = await samGate.WaitAsync(cancellationToken))
            {
                body = await PostPropagationAsync(file, previous, cancellationToken);
            }

            var propagated = ParsePropagation(body, previous);
            if (propagated.Count > 0)
            {
                using var document = JsonDocument.Parse(body);
                var request = new AnnotationSaveRequest(file.Id, document.RootElement.GetProperty("width").GetInt32(), document.RootElement.GetProperty("height").GetInt32(), propagated);
                await store.PersistAutoAnnotationAsync(video.UserId, request, file, recordTask: false);
            }

            progress = progress with { Processed = progress.Processed + 1, Succeeded = progress.Succeeded + (propagated.Count > 0 ? 1 : 0) };
            var status = await store.RecordBatchProgressAsync(task.Id, progress, targets.Count);
            if (status != "running") return;
            if (propagated.Count == 0)
            {
                progress = progress with { LastError = "所有目标都已跟丢，传播提前结束" };
                break;
            }

            previous = propagated;
        }

        await store.RecordBatchProgressAsync(task.Id, progress, targets.Count, "completed");
    }

    async Task<string> PostPropagationAsync(FileItem file, IReadOnlyList<AnnotationItem> previous, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        await using var image = await store.OpenStoredObjectAsync(file.Path);
        var imageContent = new StreamContent(image);
        imageContent.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? "image/jpeg");
        content.Add(imageContent, "image", file.Name);
        content.Add(new StringContent(JsonSerializer.Serialize(previous.Select(x => new
        {
            id = x.Id,
            box = new { cx = x.Bbox.Cx, cy = x.Bbox.Cy, width = x.Bbox.Width, height = x.Bbox.Height }
        }))), "boxes");
        using var response = await clientFactory.CreateClient("sam").PostAsync("/api/video/propagate", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new SamServiceException((int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
        return body;
    }

    // Labels follow their object: each result id refers to an annotation on the previous frame.
    public static List<AnnotationItem> ParsePropagation(string body, IReadOnlyList<AnnotationItem> previous)
    {
        using var document = JsonDocument.Parse(body);
        var byId = previous.ToDictionary(x => x.Id);
        var items = new List<AnnotationItem>();
        foreach (var result in document.RootElement.GetProperty("results").EnumerateArray())
        {
            if (result.TryGetProperty("lost", out var lost) && lost.GetBoolean()) continue;
            if (!byId.TryGetValue(result.GetProperty("id").GetString() ?? "", out var source)) continue;
            var box = result.GetProperty("yoloBox");
            var segment = result.TryGetProperty("yoloSegments", out var segments) && segments.ValueKind == JsonValueKind.Array
                ? AutoAnnotationParser.PickLargestSegment(segments.EnumerateArray().Select(x => x.EnumerateArray().Select(v => v.GetDouble()).ToList()))
                : null;
            items.Add(new AnnotationItem(
                "ann_" + Util.Id(),
                source.ClassId,
                source.Label,
                new YoloBox(box.GetProperty("cx").GetDouble(), box.GetProperty("cy").GetDouble(), box.GetProperty("width").GetDouble(), box.GetProperty("height").GetDouble()),
                segment,
                result.TryGetProperty("score", out var score) ? score.GetDouble() : 1.0));
        }

        return items;
    }
}
