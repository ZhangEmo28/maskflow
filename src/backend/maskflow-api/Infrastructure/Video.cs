using System.Text.Json;

public sealed record VideoTaskInput(string VideoId, double EverySeconds, int MaxFrames, int FromFileId, int Count)
{
    public static VideoTaskInput? From(Dictionary<string, object?>? input)
    {
        if (input is null) return null;
        var root = JsonSerializer.SerializeToElement(input);
        if (!root.TryGetProperty("videoId", out var videoId) || videoId.ValueKind != JsonValueKind.String) return null;
        double Number(string name, double fallback) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
        return new VideoTaskInput(videoId.GetString()!, Number("everySeconds", 1), (int)Number("maxFrames", 300), (int)Number("fromFileId", 0), (int)Number("count", 0));
    }
}

public sealed partial class MaskFlowStore
{
    public const string VideoFramesTaskType = "video_frames";
    public const string VideoPropagationTaskType = "video_propagation";
    static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi"];

    static long MaxVideoBytes =>
        long.TryParse(Environment.GetEnvironmentVariable("MASKFLOW_MAX_VIDEO_BYTES"), out var value) && value > 0 ? value : 500L * 1024 * 1024;

    // Container signatures: ISO-BMFF (mp4/mov) has "ftyp" (or a top-level atom) at offset 4,
    // Matroska/WebM starts with EBML 1A 45 DF A3, AVI is RIFF....AVI.
    public static bool LooksLikeVideo(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8)
        {
            var atom = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));
            if (atom is "ftyp" or "moov" or "mdat" or "wide" or "free" or "skip") return true;
        }

        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return true;
        return header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("AVI "u8);
    }

    public async Task<(VideoItem Video, TaskItem Task)> AddVideoAsync(User owner, IFormFile upload, string projectId, double everySeconds, int maxFrames)
    {
        if (!VideoExtensions.Contains(Path.GetExtension(upload.FileName).ToLowerInvariant()))
        {
            throw new BadHttpRequestException("支持的视频格式：mp4、mov、m4v、webm、mkv、avi。", 400);
        }

        if (upload.Length == 0 || upload.Length > MaxVideoBytes)
        {
            throw new BadHttpRequestException($"视频大小需在 {MaxVideoBytes / (1024 * 1024)} MB 以内。", 413);
        }

        var header = new byte[12];
        await using (var stream = upload.OpenReadStream())
        {
            _ = await stream.ReadAsync(header);
        }

        if (!LooksLikeVideo(header)) throw new BadHttpRequestException("文件内容不是有效的视频。", 400);
        everySeconds = Math.Clamp(double.IsFinite(everySeconds) ? everySeconds : 1, 0.04, 3600);
        maxFrames = Math.Clamp(maxFrames, 1, 2000);
        EnsureStorageAvailable(GetUser(owner.Id)!, upload.Length);

        var path = await PersistUploadAsync(upload, owner.Id, projectId, Path.GetFileName(upload.FileName));
        VideoItem? video = null;
        TaskItem? task = null;
        await SaveAsync(() =>
        {
            deleteOnRollback.Add(path);
            var current = GetUser(owner.Id)!;
            EnsureStorageAvailable(current, upload.Length);
            video = new VideoItem("video_" + Util.Id(), owner.Id, projectId, Path.GetFileName(upload.FileName), path, upload.Length, 0, 0, everySeconds, [], [], "queued", null, DateTimeOffset.UtcNow);
            State.Videos.Add(video);
            task = CreateTaskCore(owner.Id, VideoFramesTaskType, $"视频抽帧 {video.Name}", projectId, null, 0,
                new Dictionary<string, object?> { ["videoId"] = video.Id, ["everySeconds"] = everySeconds, ["maxFrames"] = maxFrames });
            ReplaceUser(current with { UsedBytes = current.UsedBytes + upload.Length });
        });
        return (video!, task!);
    }

    public VideoTaskInput? GetVideoTaskInput(TaskItem task) =>
        VideoTaskInput.From(State.Jobs.FirstOrDefault(x => x.Id == task.JobId)?.Input);

    public VideoItem? VideoForFile(int fileId) => State.Videos.FirstOrDefault(x => x.FrameFileIds.Contains(fileId));

    public async Task CommitVideoFramesAsync(string videoId, IReadOnlyList<(int Index, string Path, long Size, string Phash)> frames, double fps, double duration)
    {
        await SaveAsync(() =>
        {
            deleteOnRollback.AddRange(frames.Select(x => x.Path));
            var video = State.Videos.FirstOrDefault(x => x.Id == videoId) ?? throw new BadHttpRequestException("视频已被删除。", 404);
            var owner = GetUser(video.UserId)!;
            var bytes = frames.Sum(x => x.Size);
            EnsureStorageAvailable(owner, bytes);
            var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(video.Name));
            var now = DateTimeOffset.UtcNow;
            var fileIds = new List<int>();
            foreach (var frame in frames)
            {
                var fileId = State.NextFileId++;
                State.Files.Add(new FileItem(fileId, video.UserId, video.ProjectId, $"{stem}_f{frame.Index:000000}.jpg", frame.Path, frame.Size, "image", "image/jpeg", now, $"/api/files/{fileId}/download", frame.Phash));
                fileIds.Add(fileId);
            }

            State.Videos.Remove(video);
            State.Videos.Add(video with { FrameFileIds = fileIds, FrameIndexes = frames.Select(x => x.Index).ToList(), Fps = fps, DurationSec = duration, Status = "ready", Error = null });
            ReplaceUser(owner with { UsedBytes = owner.UsedBytes + bytes });
        });
    }

    public Task SetVideoStatusAsync(string videoId, string status, string? error = null) =>
        SaveAsync(() =>
        {
            var video = State.Videos.FirstOrDefault(x => x.Id == videoId);
            if (video is null) return;
            State.Videos.Remove(video);
            State.Videos.Add(video with { Status = status, Error = error });
        });

    // Deletes the source video; extracted frames stay as ordinary images with their annotations.
    public async Task<bool> DeleteVideoAsync(string projectId, string videoId)
    {
        var deleted = false;
        await SaveAsync(() =>
        {
            var video = State.Videos.FirstOrDefault(x => x.Id == videoId && x.ProjectId == projectId);
            if (video is null) return;
            State.Videos.Remove(video);
            deleteAfterCommit.Add(video.Path);
            var owner = GetUser(video.UserId);
            if (owner is not null) ReplaceUser(owner with { UsedBytes = Math.Max(0, owner.UsedBytes - video.Size) });
            deleted = true;
        });
        return deleted;
    }

    public async Task<TaskItem> CreatePropagationTaskAsync(int ownerId, VideoItem video, int fromFileId, int count)
    {
        TaskItem? task = null;
        await SaveAsync(() =>
        {
            task = CreateTaskCore(ownerId, VideoPropagationTaskType, $"视频传播 {video.Name}（{count} 帧）", video.ProjectId, fromFileId, count,
                new Dictionary<string, object?> { ["videoId"] = video.Id, ["fromFileId"] = fromFileId, ["count"] = count });
            task = ApplyTaskUpdate(ownerId, task.Id, "queued", 0, null, BatchProgress.Empty.ToResult(count))!;
        });
        return task!;
    }

    internal Task<string> PersistFrameAsync(byte[] bytes, int ownerId, string projectId, string name) =>
        PersistBytesAsync(bytes, ownerId, projectId, name, "image/jpeg");

    internal Task DeleteObjectsQuietlyAsync(IEnumerable<string> paths) => DeleteStoredObjectsQuietlyAsync(paths);
}
