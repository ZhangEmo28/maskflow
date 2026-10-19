using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace MaskFlow.Api.Tests;

// Annotation status rules, empty AI results, bulk operations and the training data check.
public class AnnotationStatusTests
{
    const string NoMasks = """{ "width": 100, "height": 80, "masks": [] }""";

    static AnnotationItem Car(bool confirmed = true) => new("c" + Guid.NewGuid().ToString("N")[..4], 0, "car", new YoloBox(0.3, 0.3, 0.2, 0.2), null, 0.9, confirmed);

    [Fact]
    public void Status_DistinguishesEmptyFromBackground()
    {
        AnnotationSet Set(List<AnnotationItem> items, bool background = false) =>
            new("s", 1, 1, 10, 10, items, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Background: background);

        Assert.Equal("unannotated", MaskFlowStore.AnnotationStatus(null));
        Assert.Equal("unannotated", MaskFlowStore.AnnotationStatus(Set([])));
        Assert.Equal("background", MaskFlowStore.AnnotationStatus(Set([], background: true)));
        Assert.Equal("pending", MaskFlowStore.AnnotationStatus(Set([Car(), Car(confirmed: false)])));
        Assert.Equal("confirmed", MaskFlowStore.AnnotationStatus(Set([Car()])));
    }

    [Fact]
    public async Task AiFindingNothing_LeavesTheImageUnannotatedAndFree()
    {
        var (store, user, _, files, _) = await AiFixture.CreateProjectAsync(imageCount: 1);
        var annotator = new AutoAnnotationService(store, new FakeSam((_, _) => (HttpStatusCode.OK, NoMasks)));

        var result = await annotator.RunAsync(user.Id, files[0], AutoAnnotationOptions.Create("concepts", 0.25, null, null), recordTask: true, CancellationToken.None);

        Assert.Equal(0, result.Detected);
        Assert.Null(store.GetAnnotationSet(user.Id, files[0].Id));
        Assert.Equal(0, store.GetQuota(store.GetUser(user.Id)!).DailyUsed);
    }

    [Fact]
    public async Task AiFindingNothing_KeepsExistingWorkUntouched()
    {
        var (store, user, _, files, _) = await AiFixture.CreateProjectAsync(imageCount: 1);
        var existing = await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[0].Id, 100, 80, [Car(confirmed: false)]));
        var annotator = new AutoAnnotationService(store, new FakeSam((_, _) => (HttpStatusCode.OK, NoMasks)));

        await annotator.RunAsync(user.Id, files[0], AutoAnnotationOptions.Create("concepts", 0.25, null, null), recordTask: true, CancellationToken.None);

        Assert.Same(existing, store.GetAnnotationSet(user.Id, files[0].Id));
    }

    [Fact]
    public async Task Batch_CountsImagesWithoutDetectionsSeparately()
    {
        var (store, user, project, files, _) = await AiFixture.CreateProjectAsync(imageCount: 3);
        var call = 0;
        var sam = new FakeSam((_, _) => (HttpStatusCode.OK, ++call == 2 ? AiFixture.ConceptResponse : NoMasks));
        var worker = AiFixture.Worker(store, sam);
        var task = await worker.QueueAsync(store.RequireProjectAccess(user.Id, project.Id, Permissions.Edit), new AnnotationBatchRequest(project.Id, Mode: "concepts"));

        await worker.RunNextAsync(CancellationToken.None);

        var done = store.State.Tasks.Single(x => x.Id == task.Id);
        var progress = BatchProgress.From(done.Result);
        Assert.Equal(("completed", 3, 1, 2, 0), (done.Status, progress.Processed, progress.Succeeded, progress.NoTargets, progress.Failed));
        Assert.Equal(["unannotated", "pending", "unannotated"], files.Select(f => MaskFlowStore.AnnotationStatus(store.GetAnnotationSet(user.Id, f.Id))));
        Assert.Equal(1, store.GetQuota(store.GetUser(user.Id)!).DailyUsed);

        // The two images are still "to do", so the next default run picks exactly them.
        var again = await worker.QueueAsync(store.RequireProjectAccess(user.Id, project.Id, Permissions.Edit), new AnnotationBatchRequest(project.Id, Mode: "concepts"));
        Assert.Equal(2, again.ImageCount);
    }

    [Fact]
    public async Task Batch_FailsWhenNothingIsDetectedAnywhere()
    {
        var (store, user, project, _, _) = await AiFixture.CreateProjectAsync(imageCount: 2);
        var worker = AiFixture.Worker(store, new FakeSam((_, _) => (HttpStatusCode.OK, NoMasks)));
        var task = await worker.QueueAsync(store.RequireProjectAccess(user.Id, project.Id, Permissions.Edit), new AnnotationBatchRequest(project.Id, Mode: "concepts"));

        await worker.RunNextAsync(CancellationToken.None);

        var done = store.State.Tasks.Single(x => x.Id == task.Id);
        Assert.Equal("failed", done.Status);
        Assert.Contains("没有检测到目标", BatchProgress.From(done.Result).LastError);
    }

    [Fact]
    public async Task Batch_RunsOnSelectedImagesOnly()
    {
        var (store, user, project, files, _) = await AiFixture.CreateProjectAsync(imageCount: 3);
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[0].Id, 100, 80, [Car()]));
        var worker = AiFixture.Worker(store, new FakeSam((_, _) => (HttpStatusCode.OK, AiFixture.ConceptResponse)));
        var access = store.RequireProjectAccess(user.Id, project.Id, Permissions.Edit);

        var task = await worker.QueueAsync(access, new AnnotationBatchRequest(project.Id, Mode: "concepts", FileIds: [files[0].Id, files[2].Id]));

        Assert.Equal([files[0].Id, files[2].Id], store.GetBatchInput(task)!.FileIds);
        await Assert.ThrowsAsync<BadHttpRequestException>(() => worker.QueueAsync(access, new AnnotationBatchRequest(project.Id, FileIds: [files[1].Id, 99999])));
    }

    [Fact]
    public async Task Export_OnlyUsesRealAnnotations()
    {
        var (store, user, project, files, _) = await AiFixture.CreateProjectAsync(imageCount: 4);
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[0].Id, 100, 80, [Car()]));
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[1].Id, 100, 80, []));                    // nothing found
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[2].Id, 100, 80, [], Background: true));  // checked: no targets
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[3].Id, 100, 80, [Car(confirmed: false)]));

        var all = DatasetExportPlan.Create(store.State, user.Id, new ExportRequest(project.Id, null, null, "yolo-detect"));
        Assert.Equal([files[0].Id, files[2].Id, files[3].Id], all.Files.Select(x => x.Id));

        // With confirmed-only, an image whose targets are all unconfirmed is left out, not turned into a background image.
        var confirmed = DatasetExportPlan.Create(store.State, user.Id, new ExportRequest(project.Id, null, null, "yolo-detect", ConfirmedOnly: true));
        Assert.Equal([files[0].Id, files[2].Id], confirmed.Files.Select(x => x.Id));
    }

    [Fact]
    public async Task ClearAnnotations_ResetsStatus()
    {
        var (store, user, project, files, _) = await AiFixture.CreateProjectAsync(imageCount: 3);
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[0].Id, 100, 80, [Car(), Car(confirmed: false)]));
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[1].Id, 100, 80, [Car(confirmed: false)]));
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(files[2].Id, 100, 80, [], Background: true));
        string Status(int index) => MaskFlowStore.AnnotationStatus(store.GetAnnotationSet(user.Id, files[index].Id));

        Assert.Equal((2, 2), await store.ClearAnnotationsAsync(user.Id, project.Id, files.Select(x => x.Id).ToList(), unconfirmedOnly: true));
        Assert.Equal(("confirmed", "unannotated", "background"), (Status(0), Status(1), Status(2)));
        Assert.Single(store.GetAnnotationSet(user.Id, files[0].Id)!.Annotations);

        Assert.Equal((2, 1), await store.ClearAnnotationsAsync(user.Id, project.Id, [files[0].Id, files[2].Id], unconfirmedOnly: false));
        Assert.All(files, f => Assert.Null(store.GetAnnotationSet(user.Id, f.Id)));
    }
}

public class BulkEndpointTests(MaskFlowApp app) : IClassFixture<MaskFlowApp>
{
    static object Targets(int fileId, bool confirmed) => new
    {
        fileId, width = 64, height = 48,
        annotations = new[] { new { id = "a1", classId = 0, label = "car", bbox = new { cx = 0.5, cy = 0.5, width = 0.2, height = 0.2 }, confidence = 0.7, confirmed } }
    };

    [Fact]
    public async Task OwnersClearAndDelete_AnnotatorsMayOnlyDropUnconfirmedResults()
    {
        var owner = await app.SignUpAsync("bulk");
        var projectId = (await owner.Json(await owner.Post("/api/projects", new { name = "bulk", dataType = "detection" }))).GetProperty("project").GetProperty("id").GetString()!;
        await owner.Put($"/api/projects/{projectId}/labels", new { labels = new[] { "car" } });
        var files = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            files.Add(await owner.UploadAsync(projectId));
            (await owner.Put($"/api/annotations/file/{files[i]}", Targets(files[i], confirmed: i == 0))).EnsureSuccessStatusCode();
        }

        var annotator = await app.SignUpAsync("annotator");
        (await owner.Post($"/api/projects/{projectId}/members", new { email = annotator.Email, role = "annotator" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await annotator.Post("/api/annotations/clear", new { projectId, fileIds = files, mode = "all" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await annotator.Post("/api/files/bulk-delete", new { projectId, fileIds = files })).StatusCode);

        var cleared = await annotator.Json(await annotator.Post("/api/annotations/clear", new { projectId, fileIds = files, mode = "unconfirmed" }));
        Assert.Equal(2, cleared.GetProperty("images").GetInt32());

        async Task<Dictionary<int, string>> StatusesAsync() =>
            (await owner.Json(await owner.Get($"/api/files?projectId={projectId}"))).GetProperty("files").EnumerateArray()
                .ToDictionary(x => x.GetProperty("id").GetInt32(), x => x.GetProperty("status").GetString()!);
        var statuses = await StatusesAsync();
        Assert.Equal(("confirmed", "unannotated", "unannotated"), (statuses[files[0]], statuses[files[1]], statuses[files[2]]));

        var deleted = await owner.Json(await owner.Post("/api/files/bulk-delete", new { projectId, fileIds = new[] { files[1], files[2] } }));
        Assert.Equal(2, deleted.GetProperty("deleted").GetInt32());
        Assert.Equal([files[0]], (await StatusesAsync()).Keys);

        var stranger = await app.SignUpAsync("stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Post("/api/files/bulk-delete", new { projectId, fileIds = files })).StatusCode);
    }
}

public class TrainingReadinessTests
{
    static DatasetExportPlan Plan(List<(int Car, int Person)> images, SplitConfig? split = null, int background = 0)
    {
        var state = new MaskFlowState();
        state.Projects.Add(new Project("p", 1, "p", "", "detection", new SplitConfig(70, 20, 10), 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        state.ProjectLabels["p"] = ["car", "person"];
        var id = 1;
        var random = new Random(3);
        foreach (var (car, person) in images.Concat(Enumerable.Repeat((0, 0), background)))
        {
            // Distinct hashes so near-duplicate grouping does not interfere.
            state.Files.Add(new FileItem(id, 1, "p", $"{id}.jpg", "", 1, "image", "image/jpeg", DateTimeOffset.UtcNow, "", random.NextInt64().ToString("x16")));
            var items = Enumerable.Range(0, car).Select(i => new AnnotationItem($"c{i}", 0, "car", new YoloBox(0.3, 0.3, 0.2, 0.2), null, 1, true))
                .Concat(Enumerable.Range(0, person).Select(i => new AnnotationItem($"p{i}", 1, "person", new YoloBox(0.7, 0.7, 0.05, 0.05), null, 1, true)))
                .ToList();
            state.AnnotationSets.Add(new AnnotationSet($"s{id}", 1, id, 100, 100, items, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Background: items.Count == 0));
            id++;
        }

        return DatasetExportPlan.Create(state, 1, new ExportRequest("p", null, split, "yolo-detect"));
    }

    [Fact]
    public void BlocksTooFewImagesAndSuggestsWhatToAdd()
    {
        var images = Enumerable.Repeat((1, 0), 30).Concat(Enumerable.Repeat((0, 1), 4)).ToList();
        var report = TrainingReadiness.Evaluate(Plan(images), new SplitConfig(70, 20, 10), 42);

        Assert.False(report.Ok);
        Assert.Contains(report.Blockers, x => x.Contains("类别「person」只有 4 张图片"));
        var person = report.Classes.Single(x => x.Label == "person");
        Assert.Equal((4, 46), (person.Images, person.AddImages));
        Assert.Contains(report.Suggestions, x => x.Contains("再补充约 46 张包含「person」的图片") && x.Contains("目标普遍很小"));
        Assert.Contains(report.Warnings, x => x.Contains("类别数量不平衡"));
    }

    [Fact]
    public void ReportsHowEachClassIsSplitAndAdvisesBackgroundImages()
    {
        var images = Enumerable.Repeat((1, 0), 60).Concat(Enumerable.Repeat((1, 1), 40)).ToList();
        var report = TrainingReadiness.Evaluate(Plan(images), new SplitConfig(70, 20, 10), 42);

        Assert.True(report.Ok, string.Join(" ", report.Blockers));
        var person = report.Classes.Single(x => x.Label == "person");
        Assert.Equal(40, person.Train + person.Val + person.Test);
        Assert.InRange(person.Train, 24, 32); // stratified: close to 70%
        Assert.InRange(person.Val, 5, 11);
        Assert.Contains(report.Suggestions, x => x.Contains("背景图"));
        Assert.Equal((100, 0), (report.Images, report.BackgroundImages));
    }

    [Fact]
    public void WarnsAboutTooManyBackgroundImages()
    {
        var report = TrainingReadiness.Evaluate(Plan(Enumerable.Repeat((1, 1), 30).ToList(), background: 20), new SplitConfig(70, 20, 10), 42);
        Assert.Contains(report.Warnings, x => x.Contains("背景图（无目标）有 20 张"));
    }
}
