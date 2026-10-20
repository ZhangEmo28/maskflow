using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace MaskFlow.Api.Tests;

// The real API with fake SAM and trainer services behind it.
public sealed class McpApp : MaskFlowApp
{
    public const string Predictions = """
    { "width": 64, "height": 48, "classNames": ["car", "person"], "detections": [
      { "className": "car", "score": 0.91, "yoloBox": { "cx": 0.3, "cy": 0.3, "width": 0.2, "height": 0.2 }, "yoloSegments": [] },
      { "className": "person", "score": 0.4, "yoloBox": { "cx": 0.8, "cy": 0.8, "width": 0.1, "height": 0.1 }, "yoloSegments": [] }
    ] }
    """;

    public FakeTrainer Trainer { get; } = new();

    public FakeSam Sam { get; } = new((request, _) => request.RequestUri!.AbsolutePath switch
    {
        "/api/annotation/concepts" => (HttpStatusCode.OK, AiFixture.ConceptResponse),
        "/api/yolo/predict" => (HttpStatusCode.OK, Predictions),
        var path when path.StartsWith("/api/yolo/models/") && request.Method == HttpMethod.Get => (HttpStatusCode.OK, """{"cached": false}"""),
        var path when path.StartsWith("/api/yolo/models/") => (HttpStatusCode.OK, """{"cached": true}"""),
        _ => (HttpStatusCode.NotFound, """{"detail":"not faked"}""")
    });

    protected override void ConfigureFakes(IServiceCollection services)
    {
        services.AddHttpClient("sam").ConfigurePrimaryHttpMessageHandler(() => Sam).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddHttpClient("trainer").ConfigurePrimaryHttpMessageHandler(() => Trainer).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
    }
}

public class McpEndpointTests(McpApp app) : IClassFixture<McpApp>
{
    static async Task<McpClient> ConnectAsync(HttpClient http) =>
        await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, NullLoggerFactory.Instance, ownsHttpClient: false));

    static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));

    static async Task<JsonElement> Call(McpClient client, string tool, object? args = null)
    {
        var result = await client.CallToolAsync(tool, Args(args));
        Assert.False(result.IsError == true, $"{tool} failed: {Text(result)}");
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    static async Task<string> CallError(McpClient client, string tool, object? args = null)
    {
        var result = await client.CallToolAsync(tool, Args(args));
        Assert.True(result.IsError == true, $"{tool} should have failed: {Text(result)}");
        return Text(result);
    }

    static Dictionary<string, object?> Args(object? args) =>
        args is null ? [] : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;

    async Task<(Client User, McpClient Mcp, string ProjectId, List<int> Files)> ProjectWithImagesAsync(int images)
    {
        var user = await app.SignUpAsync("agent");
        var mcp = await ConnectAsync(user.Http);
        var created = await Call(mcp, "create_project", new { name = "工地", labels = new[] { "car", "person" } });
        var projectId = created.GetProperty("project").GetProperty("id").GetString()!;
        var files = new List<int>();
        for (var i = 0; i < images; i++) files.Add(await user.UploadAsync(projectId));
        return (user, mcp, projectId, files);
    }

    [Fact]
    public async Task Unauthenticated_RequestsGetAnOAuthChallenge()
    {
        var response = await app.CreateClient().PostAsync("/mcp", new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=", response.Headers.WwwAuthenticate.ToString());
        Assert.Contains("/.well-known/oauth-protected-resource", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task ApiTokens_WorkAndToolsCarryHints()
    {
        var user = await app.SignUpAsync("token");
        var created = await user.Json(await user.Post("/api/account/api-tokens", new { name = "Claude" }));
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.GetProperty("value").GetString());
        var mcp = await ConnectAsync(http);

        var tools = (await mcp.ListToolsAsync()).ToDictionary(x => x.Name);
        foreach (var name in new[]
                 {
                     "list_projects", "get_project", "create_project", "update_labels", "check_quality", "export_dataset", "list_models",
                     "list_base_weights", "list_images", "get_annotations", "view_image", "auto_annotate_image", "auto_annotate_project",
                     "get_task", "edit_annotations", "confirm_annotations", "clear_annotations", "review_image", "evaluate_model", "check_training_data", "start_training",
                     "list_trainings", "get_training", "cancel_training", "view_training_plot"
                 })
        {
            Assert.True(tools.ContainsKey(name), $"missing tool {name}");
        }

        Assert.True(tools["list_projects"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["edit_annotations"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.Contains("\"box\"", tools["edit_annotations"].ProtocolTool.InputSchema.GetRawText());
        Assert.Equal(0, (await Call(mcp, "list_projects")).GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task Agent_AnnotatesLooksAtAndFixesImages()
    {
        var (_, mcp, projectId, files) = await ProjectWithImagesAsync(2);
        var prompts = await Call(mcp, "update_labels", new { project_id = projectId, add_labels = new[] { "安全帽" }, prompts = new Dictionary<string, string> { ["安全帽"] = "hard hat" } });
        Assert.Equal("hard hat", prompts.GetProperty("prompts").GetProperty("安全帽").GetString());
        Assert.Equal(2, (await Call(mcp, "list_images", new { project_id = projectId, status = "unannotated" })).GetProperty("total").GetInt32());

        var annotated = await Call(mcp, "auto_annotate_image", new { file_id = files[0] });
        Assert.Equal(2, annotated.GetProperty("targets").GetInt32());

        var view = await mcp.CallToolAsync("view_image", Args(new { file_id = files[0], max_side = 512 }));
        var image = Assert.Single(view.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/jpeg", image.MimeType);
        using (var bitmap = SKBitmap.Decode(image.DecodedData.ToArray()))
        {
            Assert.Equal((64, 48), (bitmap.Width, bitmap.Height)); // never upscaled
        }

        Assert.Contains("#1 car", Text(view));
        Assert.Contains("未确认", Text(view));

        var targets = (await Call(mcp, "get_annotations", new { file_id = files[0] })).GetProperty("targets").EnumerateArray().ToList();
        var person = targets.Single(x => x.GetProperty("label").GetString() == "person").GetProperty("id").GetString();
        var car = targets.Single(x => x.GetProperty("label").GetString() == "car").GetProperty("id").GetString();

        var error = await CallError(mcp, "edit_annotations", new { file_id = files[0], add = new[] { new { label = "truck", box = new[] { 0.1, 0.1, 0.2, 0.2 } } } });
        Assert.Contains("car、person、安全帽", error);

        var edited = await Call(mcp, "edit_annotations", new
        {
            file_id = files[0],
            add = new[] { new { label = "安全帽", box = new[] { 0.6, 0.1, 0.7, 0.2 } } },
            update = new[] { new { id = car, box = new[] { 0.2, 0.2, 0.5, 0.5 } } },
            delete = new[] { person }
        });
        var after = edited.GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(["car", "安全帽"], after.Select(x => x.GetProperty("label").GetString()));
        Assert.Equal([0.2, 0.2, 0.5, 0.5], after[0].GetProperty("box").EnumerateArray().Select(x => x.GetDouble()));
        Assert.All(after, x => Assert.False(x.GetProperty("confirmed").GetBoolean()));

        var quality = await Call(mcp, "check_quality", new { project_id = projectId });
        Assert.Equal(1, quality.GetProperty("unannotatedImages").GetInt32());
        Assert.Contains(quality.GetProperty("issues").EnumerateArray(), x => x.GetProperty("kind").GetString() == "label_sparse");

        var confirmed = await Call(mcp, "confirm_annotations", new { project_id = projectId, labels = new[] { "car" } });
        Assert.Equal(1, confirmed.GetProperty("confirmedTargets").GetInt32());
        var project = await Call(mcp, "get_project", new { project_id = projectId });
        Assert.Equal((2, 1), (project.GetProperty("targets").GetInt32(), project.GetProperty("confirmedTargets").GetInt32()));
        Assert.Equal(1, project.GetProperty("aiQuota").GetProperty("dailyUsed").GetInt32());
    }

    [Fact]
    public async Task Strangers_GetNotFound()
    {
        var (_, _, projectId, files) = await ProjectWithImagesAsync(1);
        var stranger = await ConnectAsync((await app.SignUpAsync("stranger")).Http);
        Assert.Contains("找不到", await CallError(stranger, "get_project", new { project_id = projectId }));
        Assert.Contains("找不到", await CallError(stranger, "view_image", new { file_id = files[0] }));
        Assert.Contains("找不到", await CallError(stranger, "edit_annotations", new { file_id = files[0], delete = new[] { "x" } }));
    }

    [Fact]
    public async Task Agent_TrainsReadsResultsAndEvaluatesTheModel()
    {
        var (user, mcp, projectId, files) = await ProjectWithImagesAsync(20);
        var tooEarly = await Call(mcp, "check_training_data", new { project_id = projectId });
        Assert.False(tooEarly.GetProperty("ok").GetBoolean());
        Assert.Contains("至少需要 20 张", tooEarly.GetProperty("blockers")[0].GetString());

        // Every image has a car; every second one also a person (20 car / 10 person images).
        for (var i = 0; i < files.Count; i++)
        {
            var targets = new List<object> { new { id = "car", classId = 0, label = "car", bbox = new { cx = 0.3, cy = 0.3, width = 0.2, height = 0.2 }, confidence = 1.0, confirmed = true } };
            if (i % 2 == 1) targets.Add(new { id = "person", classId = 1, label = "person", bbox = new { cx = 0.8, cy = 0.8, width = 0.1, height = 0.1 }, confidence = 1.0, confirmed = true });
            (await user.Put($"/api/annotations/file/{files[i]}", new { fileId = files[i], width = 64, height = 48, annotations = targets })).EnsureSuccessStatusCode();
        }

        var ready = await Call(mcp, "check_training_data", new { project_id = projectId, base_weight = "yolo11n.pt", train = 75, val = 25, test = 0 });
        Assert.True(ready.GetProperty("ok").GetBoolean(), ready.GetRawText());
        Assert.Contains(ready.GetProperty("suggestions").EnumerateArray(), x => x.GetString()!.Contains("背景图"));

        var weights = await Call(mcp, "list_base_weights");
        Assert.True(weights.GetProperty("available").GetBoolean());
        Assert.Contains("需要分割项目", await CallError(mcp, "start_training", new { project_id = projectId, base_weight = "yolo11n-seg.pt" }));

        var started = await Call(mcp, "start_training", new { project_id = projectId, base_weight = "yolo11n.pt", epochs = 3, train = 75, val = 25, test = 0 });
        var trainingId = started.GetProperty("trainingId").GetString()!;
        var worker = app.Services.GetRequiredService<TrainingWorker>();
        await WaitForAsync(async () => (await Call(mcp, "get_training", new { training_id = trainingId })).GetProperty("status").GetString() == "training", worker);

        app.Trainer.Complete(trainingId);
        await WaitForAsync(async () => (await Call(mcp, "get_training", new { training_id = trainingId })).GetProperty("status").GetString() == "completed", worker);

        var training = await Call(mcp, "get_training", new { training_id = trainingId });
        Assert.Equal(0.41, training.GetProperty("headline").GetProperty("mAP50-95").GetDouble());
        Assert.Equal(3, training.GetProperty("curve").GetArrayLength());
        var modelId = training.GetProperty("modelId").GetString();
        Assert.NotNull(modelId);

        var plot = await mcp.CallToolAsync("view_training_plot", Args(new { training_id = trainingId, plot = "confusion_matrix" }));
        Assert.Equal("image/png", Assert.Single(plot.Content.OfType<ImageContentBlock>()).MimeType);

        // Predictions: a car where every image has one, and a person at the spot only odd images have one.
        var evaluation = await Call(mcp, "evaluate_model", new { model_id = modelId, project_id = projectId, max_images = 4 });
        var overall = evaluation.GetProperty("overall");
        Assert.Equal((6, 2, 0), (overall.GetProperty("tp").GetInt32(), overall.GetProperty("fp").GetInt32(), overall.GetProperty("fn").GetInt32()));
        Assert.Equal(2, evaluation.GetProperty("worstImages").GetArrayLength());
        var models = await Call(mcp, "list_models", new { project_id = projectId });
        Assert.Equal(trainingId, models.GetProperty("models")[0].GetProperty("trainingId").GetString());

        // Download links work without the auth header (browser download manager), only for their target.
        var link = (await user.Json(await user.Post($"/api/trainings/{trainingId}/download-link"))).GetProperty("url").GetString()!;
        var anonymous = app.CreateClient();
        var bundle = await anonymous.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        Assert.Equal("application/zip", bundle.Content.Headers.ContentType!.MediaType);
        using (var zip = new System.IO.Compression.ZipArchive(await bundle.Content.ReadAsStreamAsync()))
        {
            Assert.Contains(zip.Entries, x => x.FullName == "runs/train/weights/best.pt");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(link[..^2] + "00")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(link.Replace("/bundle?", "/artifacts/train/weights/best.pt?"))).StatusCode);
        var weightLink = (await user.Json(await user.Post($"/api/trainings/{trainingId}/download-link?path=train/weights/best.pt"))).GetProperty("url").GetString()!;
        Assert.Equal(1000, (await anonymous.GetByteArrayAsync(weightLink)).Length);
        var stranger = await app.SignUpAsync("outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Post($"/api/trainings/{trainingId}/download-link")).StatusCode);
    }

    static async Task WaitForAsync(Func<Task<bool>> condition, TrainingWorker worker)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            worker.Wake();
            await Task.Delay(100);
        }

        Assert.Fail("condition not reached");
    }
}

public class AgentToolTests
{
    static AnnotationItem Item(string? label, double cx, double cy, double w, double h, double confidence = 1, bool confirmed = true, string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("N")[..6], label is null ? -1 : label == "car" ? 0 : 1, label, new YoloBox(cx, cy, w, h), null, confidence, confirmed);

    [Fact]
    public void Matching_CountsTruePositivesFalsePositivesAndMisses()
    {
        var truth = new[] { Item("car", 0.3, 0.3, 0.2, 0.2), Item("car", 0.7, 0.7, 0.2, 0.2), Item("person", 0.5, 0.5, 0.1, 0.3) };
        var predictions = new[]
        {
            Item("car", 0.31, 0.3, 0.2, 0.2, 0.9),  // matches the first car
            Item("car", 0.3, 0.3, 0.2, 0.2, 0.8),   // duplicate of an already matched car
            Item("person", 0.7, 0.7, 0.2, 0.2, 0.7) // overlaps a car, wrong class
        };

        var result = DetectionMatching.Match(truth, predictions).ToDictionary(x => x.Label);

        Assert.Equal((1, 1, 1), (result["car"].TruePositives, result["car"].FalsePositives, result["car"].FalseNegatives));
        Assert.Equal((0, 1, 1), (result["person"].TruePositives, result["person"].FalsePositives, result["person"].FalseNegatives));
        Assert.Equal(0.5, result["car"].Precision);
        Assert.Equal(0, result["person"].F1);
    }

    [Fact]
    public async Task QualityCheck_FlagsCommonProblems()
    {
        var (store, owner, project, files, _) = await AiFixture.CreateProjectAsync(imageCount: 3);
        await store.SaveAnnotationSetAsync(owner.Id, new AnnotationSaveRequest(files[0].Id, 100, 100,
        [
            Item("car", 0.3, 0.3, 0.2, 0.2, id: "a"),
            Item("car", 0.3, 0.3, 0.2, 0.21, id: "dup"),
            Item("person", 0.31, 0.3, 0.2, 0.2, id: "conflict"),
            Item(null, 0.5, 0.5, 0.1, 0.1, id: "nolabel"),
            Item("car", 0.9, 0.9, 0.01, 0.01, id: "tiny"),
            Item("car", 0.5, 0.5, 1, 0.98, 0.3, confirmed: false, id: "huge")
        ]));
        await store.SaveAnnotationSetAsync(owner.Id, new AnnotationSaveRequest(files[1].Id, 100, 100, [], Background: true));

        var report = JsonSerializer.SerializeToElement(QualityCheck.Run(store.State, project, ["car", "person", "bike"]));
        var kinds = report.GetProperty("issues").EnumerateArray()
            .GroupBy(x => x.GetProperty("Kind").GetString()!)
            .ToDictionary(x => x.Key, x => x.Select(i => i.GetProperty("AnnotationId").GetString()).ToList());

        Assert.Equal(["dup"], kinds["duplicate"]);
        Assert.Contains("conflict", kinds["label_conflict"]);
        Assert.Equal(["nolabel"], kinds["unlabeled"]);
        Assert.Equal(["tiny"], kinds["tiny_box"]);
        Assert.Equal(["huge"], kinds["huge_box"]);
        Assert.Equal(["huge"], kinds["low_confidence"]);
        Assert.Contains("label_missing", kinds.Keys);  // bike
        Assert.Equal((1, 1), (report.GetProperty("unannotatedImages").GetInt32(), report.GetProperty("backgroundImages").GetInt32()));
    }

    [Fact]
    public void Renderer_DrawsInLabelColoursAndDownscales()
    {
        var image = ImageFixture.Png(400, 200, color: SKColors.White);
        var jpeg = AnnotationRenderer.Render(image, [Item("person", 0.5, 0.5, 0.5, 0.5)], ["car", "person"], maxSide: 200);

        using var bitmap = SKBitmap.Decode(jpeg);
        Assert.Equal((200, 100), (bitmap.Width, bitmap.Height));
        // The box's left edge sits at x = 50 in label 1's colour (green #16a34a).
        var edge = bitmap.GetPixel(50, 60);
        Assert.InRange(edge.Green, 120, 200);
        Assert.InRange(edge.Red, 0, 70);
    }

    [Fact]
    public void Geometry_ParsesBoxesAndRefitsPolygons()
    {
        var box = AnnotationTools.ParseBox([0.1, 0.4, 0.4, 0.6]);
        Assert.Equal([0.25, 0.5, 0.3, 0.2], new[] { box.Cx, box.Cy, box.Width, box.Height }.Select(x => Math.Round(x, 9)));
        Assert.Throws<ModelContextProtocol.McpException>(() => AnnotationTools.ParseBox([0.5, 0.5, 0.4, 0.6]));
        Assert.Throws<ModelContextProtocol.McpException>(() => AnnotationTools.ParsePolygon([0.1, 0.1, 0.2]));
        var polygon = AnnotationTools.Refit([0.2, 0.2, 0.4, 0.2, 0.4, 0.4], new YoloBox(0.3, 0.3, 0.2, 0.2), new YoloBox(0.6, 0.6, 0.4, 0.4));
        Assert.Equal([0.4, 0.4, 0.8, 0.4, 0.8, 0.8], polygon.Select(x => Math.Round(x, 6)));
        var fitted = AnnotationTools.BoxOf(polygon);
        Assert.Equal([0.6, 0.6, 0.4, 0.4], new[] { fitted.Cx, fitted.Cy, fitted.Width, fitted.Height }.Select(x => Math.Round(x, 9)));
    }
}
