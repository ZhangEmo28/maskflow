using System.Net;
using System.Text;
using System.Text.Json;
using MaskFlow.Api.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaskFlow.Api.Tests;

public sealed class FakeEmailSender : IEmailSender
{
    public bool IsConfigured => true;
    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        Sent.Add((to, subject, body));
        return Task.CompletedTask;
    }
}

public sealed class FakeSam : HttpMessageHandler, IHttpClientFactory
{
    readonly Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> respond;
    public List<(string Method, string Path, string Body)> Calls { get; } = [];

    public FakeSam(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond) => this.respond = respond;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://sam.test") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
        var (status, response) = respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}

static class AiFixture
{
    // Two concept masks: prompt 0 (car) and prompt 1 (person), plus a lower-score duplicate of the car under "person".
    public const string ConceptResponse = """
    { "width": 100, "height": 80, "masks": [
      { "promptIndex": 0, "score": 0.9, "yoloBox": { "cx": 0.3, "cy": 0.3, "width": 0.2, "height": 0.2 }, "yoloSegments": [[0.2,0.2,0.4,0.2,0.4,0.4]] },
      { "promptIndex": 1, "score": 0.5, "yoloBox": { "cx": 0.3, "cy": 0.3, "width": 0.2, "height": 0.21 }, "yoloSegments": [] },
      { "promptIndex": 1, "score": 0.8, "yoloBox": { "cx": 0.7, "cy": 0.6, "width": 0.1, "height": 0.3 }, "yoloSegments": [] }
    ] }
    """;

    public static async Task<(MaskFlowStore Store, User User, Project Project, List<FileItem> Files, string Dir)> CreateProjectAsync(int imageCount = 3)
    {
        var (store, _) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(user.Id, new ProjectCreate("ai", null, "detection", null));
        await store.SaveProjectLabelsAsync(user.Id, project.Id, ["car", "person"]);
        var dir = Path.Combine(Path.GetTempPath(), "maskflow-ai-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var files = new List<FileItem>();
        for (var i = 0; i < imageCount; i++)
        {
            var path = Path.Combine(dir, $"{i}.jpg");
            await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF, 0xE0, (byte)i]);
            files.Add(await TestStore.AddFileAsync(store, user.Id, project.Id, path));
        }

        return (store, user, project, files, dir);
    }

    public static BatchAnnotationWorker Worker(MaskFlowStore store, FakeSam sam, FakeEmailSender? mail = null) =>
        new(store, new AutoAnnotationService(store, sam), new SamInferenceGate(),
            new AccountMailer(mail ?? new FakeEmailSender(), NullLogger<AccountMailer>.Instance),
            NullLogger<BatchAnnotationWorker>.Instance);
}

public class AiParserTests
{
    [Fact]
    public void ParseConcepts_AssignsLabelsByPromptAndDropsCrossLabelDuplicates()
    {
        var request = AutoAnnotationParser.ParseConcepts(5, AiFixture.ConceptResponse, ["car", "person"]);

        Assert.Equal(2, request.Annotations.Count);
        var car = Assert.Single(request.Annotations, x => x.Label == "car");
        Assert.Equal(0, car.ClassId);
        Assert.Equal(0.9, car.Confidence);
        Assert.Equal(6, car.Segment!.Count);
        var person = Assert.Single(request.Annotations, x => x.Label == "person");
        Assert.Equal(0.8, person.Confidence);
    }

    [Fact]
    public void ParseModelDetections_MatchesClassNamesToLabels()
    {
        const string body = """
        { "width": 10, "height": 10, "classNames": ["Person", "dog"], "detections": [
          { "className": "Person", "score": 0.7, "yoloBox": { "cx": 0.5, "cy": 0.5, "width": 0.2, "height": 0.2 } },
          { "className": "dog", "score": 0.6, "yoloBox": { "cx": 0.2, "cy": 0.2, "width": 0.1, "height": 0.1 } }
        ] }
        """;

        var request = AutoAnnotationParser.ParseModelDetections(1, body, ["car", "person"], out var classNames);

        Assert.Equal(["Person", "dog"], classNames);
        Assert.Equal(("person", 1), (request.Annotations[0].Label, request.Annotations[0].ClassId));
        Assert.Equal((null, -1), (request.Annotations[1].Label, request.Annotations[1].ClassId));
    }

    [Fact]
    public void BatchInput_SurvivesJsonRoundTrip()
    {
        var input = new Dictionary<string, object?> { ["fileIds"] = new List<int> { 3, 4 }, ["mode"] = "concepts", ["conf"] = 0.4, ["modelId"] = null };
        var reloaded = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(input))!;

        var parsed = BatchTaskInput.From(reloaded)!;

        Assert.Equal([3, 4], parsed.FileIds);
        Assert.Equal("concepts", parsed.Mode);
        Assert.Equal(0.4, parsed.Conf);
        Assert.Null(parsed.ModelId);
    }
}

public class LabelPromptTests
{
    [Fact]
    public async Task Prompts_DefaultToLabelAndArePrunedWithLabels()
    {
        var (store, user, project, _, dir) = await AiFixture.CreateProjectAsync(0);
        try
        {
            await store.SaveLabelPromptsAsync(user.Id, project.Id, new Dictionary<string, string?> { ["CAR"] = " sedan car ", ["person"] = "person", ["ghost"] = "x" });

            Assert.Equal("sedan car", store.PromptForLabel(project.Id, "car"));
            Assert.Equal("person", store.PromptForLabel(project.Id, "person"));
            Assert.Single(store.GetLabelPrompts(user.Id, project.Id));

            await store.DeleteProjectLabelAsync(user.Id, project.Id, "car");
            Assert.Empty(store.GetLabelPrompts(user.Id, project.Id));
            Assert.False(store.State.ProjectLabelPrompts.ContainsKey(project.Id));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class BatchAnnotationTests
{
    [Fact]
    public async Task Batch_AnnotatesEveryImageWithConceptLabels()
    {
        var (store, user, project, files, dir) = await AiFixture.CreateProjectAsync();
        try
        {
            await store.SaveLabelPromptsAsync(user.Id, project.Id, new Dictionary<string, string?> { ["car"] = "sedan car" });
            var sam = new FakeSam((request, _) => (HttpStatusCode.OK, AiFixture.ConceptResponse));
            var mail = new FakeEmailSender();
            var task = await store.CreateBatchAnnotationTaskAsync(user.Id,
                new AnnotationBatchRequest(project.Id, 0.3, null, "concepts"), files.Select(x => x.Id).ToList());

            Assert.True(await AiFixture.Worker(store, sam, mail).RunNextAsync(CancellationToken.None));
            var notice = Assert.Single(mail.Sent);
            Assert.Equal(user.Email, notice.To);
            Assert.Contains("成功 3", notice.Body);

            var done = store.State.Tasks.Single(x => x.Id == task.Id);
            Assert.Equal("completed", done.Status);
            Assert.Equal(1, done.Progress);
            Assert.Equal(3, BatchProgress.From(done.Result).Succeeded);
            Assert.Equal(3, sam.Calls.Count(x => x.Path == "/api/annotation/concepts"));
            Assert.Contains("[\"sedan car\",\"person\"]", sam.Calls[0].Body);
            Assert.All(files, file => Assert.Equal(["car", "person"], store.GetAnnotationSet(user.Id, file.Id)!.Annotations.Select(x => x.Label)));
            Assert.Equal(3, store.GetQuota(user).DailyUsed);
            // Batch runs do not create one task record per image.
            Assert.DoesNotContain(store.State.Tasks, x => x.Type == "yolo_auto_annotation");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Batch_HonoursCancellationAndResumesFromProgress()
    {
        var (store, user, project, files, dir) = await AiFixture.CreateProjectAsync();
        try
        {
            var sam = new FakeSam((request, _) => (HttpStatusCode.OK, AiFixture.ConceptResponse));
            var worker = AiFixture.Worker(store, sam);

            var cancelled = await store.CreateBatchAnnotationTaskAsync(user.Id, new AnnotationBatchRequest(project.Id, Mode: "concepts"), files.Select(x => x.Id).ToList());
            await store.UpdateTaskAsync(user.Id, cancelled.Id, "cancelled", 0, null);
            Assert.False(await worker.RunNextAsync(CancellationToken.None));
            Assert.Empty(sam.Calls);

            // Simulate a restart after the first image: progress says 1 processed.
            var resumed = await store.CreateBatchAnnotationTaskAsync(user.Id, new AnnotationBatchRequest(project.Id, Mode: "concepts"), files.Select(x => x.Id).ToList());
            await store.MutateAsync(state =>
            {
                var current = state.Tasks.Single(x => x.Id == resumed.Id);
                state.Tasks.Remove(current);
                state.Tasks.Add(current with { Result = new BatchProgress(1, 1, 0, null).ToResult(3) });
                return 0;
            });
            await worker.RunNextAsync(CancellationToken.None);

            Assert.Equal(2, sam.Calls.Count);
            Assert.Null(store.GetAnnotationSet(user.Id, files[0].Id));
            Assert.Equal("completed", store.State.Tasks.Single(x => x.Id == resumed.Id).Status);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Batch_StopsWhenInferenceServiceIsDown()
    {
        var (store, user, project, files, dir) = await AiFixture.CreateProjectAsync();
        try
        {
            var sam = new FakeSam((request, _) => throw new HttpRequestException("connection refused"));
            var task = await store.CreateBatchAnnotationTaskAsync(user.Id, new AnnotationBatchRequest(project.Id, Mode: "concepts"), files.Select(x => x.Id).ToList());

            await AiFixture.Worker(store, sam).RunNextAsync(CancellationToken.None);

            var failed = store.State.Tasks.Single(x => x.Id == task.Id);
            Assert.Equal("failed", failed.Status);
            Assert.Contains("推理服务不可用", failed.ErrorMessage);
            Assert.Single(sam.Calls);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ModelMode_UploadsWeightsOnceAndMapsClasses()
    {
        var (store, user, project, files, dir) = await AiFixture.CreateProjectAsync(2);
        try
        {
            var weights = Encoding.UTF8.GetBytes("fake-onnx-weights");
            await using var stream = new MemoryStream(weights);
            var upload = new Microsoft.AspNetCore.Http.FormFile(stream, 0, weights.Length, "file", "best.onnx");
            var model = await store.AddAiModelAsync(user, upload, "helmet v1", project.Id);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(weights)).ToLowerInvariant(), model.Sha256);
            Assert.Equal(weights.Length, store.GetUser(user.Id)!.UsedBytes);

            var cached = false;
            var sam = new FakeSam((request, body) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.StartsWith("/api/yolo/models/") && request.Method == HttpMethod.Get)
                {
                    return (HttpStatusCode.OK, cached ? $$"""{ "cached": true, "sha256": "{{model.Sha256}}" }""" : """{ "cached": false }""");
                }

                if (path.StartsWith("/api/yolo/models/") && request.Method == HttpMethod.Put)
                {
                    cached = true;
                    return (HttpStatusCode.OK, "{}");
                }

                return path == "/api/yolo/predict"
                    ? (HttpStatusCode.OK, """
                        { "width": 10, "height": 10, "classNames": ["person"], "detections": [
                          { "className": "person", "score": 0.66, "yoloBox": { "cx": 0.5, "cy": 0.5, "width": 0.2, "height": 0.2 } } ] }
                        """)
                    : (HttpStatusCode.NotFound, "{}");
            });
            var annotator = new AutoAnnotationService(store, sam);
            var options = AutoAnnotationOptions.Create("model", 0.25, null, model.Id);

            await annotator.RunAsync(user.Id, files[0], options, recordTask: false, CancellationToken.None);
            await annotator.RunAsync(user.Id, files[1], options, recordTask: false, CancellationToken.None);

            Assert.Single(sam.Calls, x => x.Method == "PUT");
            Assert.Contains(model.Sha256, sam.Calls.Single(x => x.Method == "PUT").Body);
            Assert.Equal("person", store.GetAnnotationSet(user.Id, files[1].Id)!.Annotations.Single().Label);
            Assert.Equal(["person"], store.State.AiModels.Single().ClassNames);

            Assert.True(await store.DeleteAiModelAsync(user.Id, model.Id));
            Assert.Empty(store.State.AiModels);
            Assert.False(File.Exists(model.Path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Models_RejectPickleCheckpoints()
    {
        var (store, user, _, _, dir) = await AiFixture.CreateProjectAsync(0);
        try
        {
            await using var stream = new MemoryStream([1, 2, 3]);
            var upload = new Microsoft.AspNetCore.Http.FormFile(stream, 0, 3, "file", "best.pt");
            var error = await Assert.ThrowsAsync<Microsoft.AspNetCore.Http.BadHttpRequestException>(() => store.AddAiModelAsync(user, upload, null, null));
            Assert.Contains("ONNX", error.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
