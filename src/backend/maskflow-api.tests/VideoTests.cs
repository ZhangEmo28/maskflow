using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using MaskFlow.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaskFlow.Api.Tests;

sealed class RawSam(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
{
    public List<string> Paths { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://sam.test") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class VideoTests
{
    static readonly byte[] Mp4Header = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];

    static async Task<(MaskFlowStore Store, User Owner, Project Project, VideoItem Video, TaskItem Task)> UploadVideoAsync()
    {
        var (store, _) = TestStore.Create();
        var owner = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(owner.Id, new ProjectCreate("video", null, "detection", null));
        await store.SaveProjectLabelsAsync(owner.Id, project.Id, ["car", "person"]);
        var bytes = Mp4Header.Concat(new byte[1000]).ToArray();
        var upload = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "street cam.mp4");
        var (video, task) = await store.AddVideoAsync(owner, upload, project.Id, 0.5, 10);
        return (store, owner, project, video, task);
    }

    static VideoTaskWorker Worker(MaskFlowStore store, RawSam sam) =>
        new(store, sam, new SamInferenceGate(), NullLogger<VideoTaskWorker>.Instance);

    static HttpResponseMessage FramesZip(params int[] indexes)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var index in indexes)
            {
                using var entry = zip.CreateEntry($"frame_{index:000000}.jpg").Open();
                entry.Write(ImageFixture.RandomPng(index, 64, 48));
            }

            using var manifest = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            manifest.Write(JsonSerializer.Serialize(new { fps = 25.0, frameCount = 100, duration = 4.0, frames = indexes.Select(i => new { index = i, time = i / 25.0, file = $"frame_{i:000000}.jpg" }) }));
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 }, true)]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x41, 0x56, 0x49, 0x20 }, true)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, false)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 }, false)]
    public void LooksLikeVideo_ChecksContainerSignatures(byte[] header, bool expected)
    {
        Assert.Equal(expected, MaskFlowStore.LooksLikeVideo(header));
    }

    [Fact]
    public async Task Upload_RejectsWrongExtensionsAndContent()
    {
        var (store, owner, project, _, _) = await UploadVideoAsync();
        var image = new FormFile(new MemoryStream(ImageFixture.Png()), 0, 100, "file", "fake.mp4");
        Assert.Equal(400, (await Assert.ThrowsAsync<BadHttpRequestException>(() => store.AddVideoAsync(owner, image, project.Id, 1, 10))).StatusCode);
        var exe = new FormFile(new MemoryStream(Mp4Header), 0, Mp4Header.Length, "file", "clip.exe");
        Assert.Equal(400, (await Assert.ThrowsAsync<BadHttpRequestException>(() => store.AddVideoAsync(owner, exe, project.Id, 1, 10))).StatusCode);
    }

    [Fact]
    public async Task FrameTask_StoresFramesInOrderAsProjectImages()
    {
        var (store, owner, project, video, task) = await UploadVideoAsync();
        var sam = new RawSam((request, body) =>
        {
            Assert.Contains("name=every_seconds", body);
            return FramesZip(0, 12, 25);
        });

        Assert.True(await Worker(store, sam).RunNextAsync(CancellationToken.None));

        var ready = store.State.Videos.Single();
        Assert.Equal(("ready", 25.0, 3), (ready.Status, ready.Fps, ready.FrameFileIds.Count));
        Assert.Equal([0, 12, 25], ready.FrameIndexes);
        var names = ready.FrameFileIds.Select(id => store.State.Files.Single(x => x.Id == id).Name).ToList();
        Assert.Equal(["street cam_f000000.jpg", "street cam_f000012.jpg", "street cam_f000025.jpg"], names);
        Assert.All(ready.FrameFileIds, id => Assert.Equal(project.Id, store.State.Files.Single(x => x.Id == id).ProjectId));
        Assert.Equal("completed", store.State.Tasks.Single(x => x.Id == task.Id).Status);
        Assert.Equal(store.State.Files.Where(x => x.ProjectId == project.Id).Sum(x => x.Size) + video.Size, store.GetUser(owner.Id)!.UsedBytes);
    }

    [Fact]
    public async Task FrameTask_FailsCleanlyWhenTheVideoCannotBeDecoded()
    {
        var (store, _, _, _, task) = await UploadVideoAsync();
        var sam = new RawSam((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"detail":"无法解码视频"}""", Encoding.UTF8, "application/json") });

        await Worker(store, sam).RunNextAsync(CancellationToken.None);

        Assert.Equal(("failed", "无法解码视频"), (store.State.Videos.Single().Status, store.State.Videos.Single().Error));
        Assert.Equal("failed", store.State.Tasks.Single(x => x.Id == task.Id).Status);
        Assert.Empty(store.State.Files);
    }

    [Fact]
    public async Task Propagation_CarriesLabelsForwardAndStopsWhenLost()
    {
        var (store, owner, _, _, _) = await UploadVideoAsync();
        await Worker(store, new RawSam((_, _) => FramesZip(0, 1, 2, 3))).RunNextAsync(CancellationToken.None);
        var frames = store.State.Videos.Single().FrameFileIds;
        await store.SaveAnnotationSetAsync(owner.Id, new AnnotationSaveRequest(frames[0], 64, 48,
        [
            new AnnotationItem("car-0", 0, "car", new YoloBox(0.3, 0.5, 0.2, 0.2), null, 1, true),
            new AnnotationItem("raw", -1, null, new YoloBox(0.8, 0.5, 0.1, 0.1), null, 1, false)
        ]));

        var call = 0;
        var sam = new RawSam((_, body) =>
        {
            call++;
            Assert.DoesNotContain("\"raw\"", body);
            var id = JsonDocument.Parse(ExtractField(body, "boxes")).RootElement[0].GetProperty("id").GetString();
            return RawSam.Json(call < 3
                ? $$"""{ "width": 64, "height": 48, "results": [{ "id": "{{id}}", "lost": false, "score": 0.7, "yoloBox": { "cx": {{0.3 + call * 0.05}}, "cy": 0.5, "width": 0.2, "height": 0.2 }, "yoloSegments": [] }] }"""
                : $$"""{ "width": 64, "height": 48, "results": [{ "id": "{{id}}", "lost": true }] }""");
        });
        var task = await store.CreatePropagationTaskAsync(owner.Id, store.State.Videos.Single(), frames[0], 3);

        await Worker(store, sam).RunNextAsync(CancellationToken.None);

        var first = store.GetAnnotationSet(owner.Id, frames[1])!.Annotations.Single();
        var second = store.GetAnnotationSet(owner.Id, frames[2])!.Annotations.Single();
        Assert.Equal(("car", 0, false, 0.7), (first.Label, first.ClassId, first.Confirmed, first.Confidence));
        Assert.Equal(0.35, first.Bbox.Cx, 6);
        Assert.Equal(0.4, second.Bbox.Cx, 6);
        Assert.Null(store.GetAnnotationSet(owner.Id, frames[3]));
        var done = store.State.Tasks.Single(x => x.Id == task.Id);
        Assert.Equal("completed", done.Status);
        var progress = BatchProgress.From(done.Result);
        Assert.Equal((3, 2), (progress.Processed, progress.Succeeded));
        Assert.Contains("跟丢", progress.LastError);
        // Only frames that produced annotations consume AI quota.
        Assert.Equal(2, store.GetQuota(owner).DailyUsed);
    }

    static string ExtractField(string multipart, string name)
    {
        var marker = $"name={name}";
        var start = multipart.IndexOf(marker, StringComparison.Ordinal);
        var body = multipart[(multipart.IndexOf("\r\n\r\n", start, StringComparison.Ordinal) + 4)..];
        return body[..body.IndexOf("\r\n--", StringComparison.Ordinal)];
    }
}
