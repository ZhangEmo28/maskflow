using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaskFlow.Api.Tests;

// Speaks the trainer service's HTTP API from memory; tests script each run's phase.
public sealed class FakeTrainer : HttpMessageHandler, IHttpClientFactory
{
    public sealed class Run
    {
        public string Phase = "training";
        public List<Dictionary<string, double>> History = [];
        public string? Error;
        public object? Metrics;
        public Dictionary<string, byte[]> Artifacts = [];
    }

    public List<object> Weights { get; } =
    [
        new { name = "yolo11n.pt", size = 5_400_000, task = "detect" },
        new { name = "yolo11n-seg.pt", size = 6_000_000, task = "segment" },
        new { name = "yolo11n-cls.pt", size = 5_000_000, task = "classify" },
        new { name = "yolo11n-pose.pt", size = 6_000_000, task = "pose" }
    ];

    public Dictionary<string, Run> Runs { get; } = [];
    public Dictionary<string, (Dictionary<string, string> Fields, byte[] Dataset)> Uploads { get; } = [];
    public List<string> Calls { get; } = [];
    public string? Running { get; set; }
    public bool Down { get; set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://trainer.test") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add($"{request.Method} {path}");
        if (Down) throw new HttpRequestException("Connection refused");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (request.Method.Method, parts)
        {
            case ("GET", ["api", "weights"]):
                return Json(new { weights = Weights, directory = "/weights" });
            case ("GET", ["api", "status"]):
                return Json(new { running = Running });
            case ("POST", ["api", "train"]):
            {
                if (Running is not null) return Json(new { detail = "busy" }, HttpStatusCode.Conflict);
                var form = (MultipartFormDataContent)request.Content!;
                var fields = new Dictionary<string, string>();
                byte[] dataset = [];
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "dataset") dataset = await part.ReadAsByteArrayAsync(cancellationToken);
                    else fields[name] = await part.ReadAsStringAsync(cancellationToken);
                }

                Uploads[fields["run_id"]] = (fields, dataset);
                Runs[fields["run_id"]] = new Run();
                Running = fields["run_id"];
                return Json(new { runId = fields["run_id"], status = "running" });
            }
            case ("GET", ["api", "train", var id]):
                if (!Runs.TryGetValue(id, out var run)) return Json(new { detail = "Run not found." }, HttpStatusCode.NotFound);
                return Json(new { runId = id, phase = run.Phase, epoch = run.History.Count, history = run.History, metrics = run.Metrics, error = run.Error, artifacts = run.Artifacts.Keys });
            case ("GET", ["api", "train", var id, "artifacts", var name]):
                return Runs.TryGetValue(id, out var owner) && owner.Artifacts.TryGetValue(name, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : Json(new { detail = "Artifact not available." }, HttpStatusCode.NotFound);
            case ("POST", ["api", "train", var id, "cancel"]):
                if (Runs.TryGetValue(id, out var cancelled)) cancelled.Phase = "cancelled";
                if (Running == id) Running = null;
                return Json(new { runId = id, phase = "cancelled" });
            case ("DELETE", ["api", "train", var id]):
                Runs.Remove(id);
                if (Running == id) Running = null;
                return Json(new { deleted = true });
            default:
                return Json(new { detail = "Not found" }, HttpStatusCode.NotFound);
        }
    }

    // Finishes a run the way train_runner does: weights, plots and metrics on disk, phase completed.
    public void Complete(string id, int epochs = 3)
    {
        var run = Runs[id];
        run.History = Enumerable.Range(1, epochs).Select(e => new Dictionary<string, double> { ["epoch"] = e, ["train/box_loss"] = 1.0 / e, ["metrics/mAP50(B)"] = 0.2 * e }).ToList();
        run.Metrics = new
        {
            baseWeight = "yolo11n.pt",
            task = "detect",
            names = new[] { "car", "person" },
            val = new
            {
                overall = new Dictionary<string, double> { ["metrics/precision(B)"] = 0.71, ["metrics/recall(B)"] = 0.62, ["metrics/mAP50(B)"] = 0.6, ["metrics/mAP50-95(B)"] = 0.41 },
                perClass = new[] { new { @class = "car", mAP50 = 0.7, mAP5095 = 0.5 } }
            },
            onnx = "best.onnx"
        };
        run.Artifacts = new()
        {
            ["best.pt"] = new byte[1000], ["last.pt"] = new byte[1000], ["best.onnx"] = new byte[800],
            ["results.csv"] = Encoding.UTF8.GetBytes("epoch,metrics/mAP50(B)\n1,0.2\n"), ["confusion_matrix.png"] = new byte[50], ["metrics.json"] = Encoding.UTF8.GetBytes("{}")
        };
        run.Phase = "completed";
        if (Running == id) Running = null;
    }

    static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

public class TrainingTests
{
    static async Task<(MaskFlowStore Store, User Owner, Project Project, FakeTrainer Trainer, TrainingWorker Worker, FakeEmailSender Mail)> SetupAsync(
        int images = 24, string dataType = "detection")
    {
        var (store, _) = TestStore.Create();
        var owner = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(owner.Id, new ProjectCreate("streets", null, dataType, null));
        await store.SaveProjectLabelsAsync(owner.Id, project.Id, ["car", "person"]);
        var dir = Path.Combine(Path.GetTempPath(), "maskflow-train-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (var i = 0; i < images; i++)
        {
            var path = Path.Combine(dir, $"{i}.png");
            await File.WriteAllBytesAsync(path, ImageFixture.RandomPng(i + 1));
            var file = await TestStore.AddFileAsync(store, owner.Id, project.Id, path);
            await store.SaveAnnotationSetAsync(owner.Id, new AnnotationSaveRequest(file.Id, 200, 100,
            [
                new AnnotationItem($"a{i}", i % 2, i % 2 == 0 ? "car" : "person", new YoloBox(0.5, 0.5, 0.2, 0.2), [0.4, 0.4, 0.6, 0.4, 0.6, 0.6], 1, true)
            ]));
        }

        var trainer = new FakeTrainer();
        var mail = new FakeEmailSender();
        var worker = new TrainingWorker(store, trainer, new AccountMailer(mail, NullLogger<AccountMailer>.Instance), NullLogger<TrainingWorker>.Instance);
        return (store, owner, project, trainer, worker, mail);
    }

    static ProjectAccess Owner(MaskFlowStore store, User owner, Project project) => store.RequireProjectAccess(owner.Id, project.Id, Permissions.Export);

    static async Task<TrainingRun> QueueAsync(MaskFlowStore store, User owner, Project project, FakeTrainer trainer, TrainingRequest? request = null)
    {
        var weights = await new TrainingWorker(store, trainer, null!, NullLogger<TrainingWorker>.Instance).ListWeightsAsync(CancellationToken.None);
        request ??= new TrainingRequest("yolo11n.pt", Epochs: 3, ImageSize: 320, Batch: 4);
        return await store.CreateTrainingRunAsync(owner.Id, Owner(store, owner, project), request, weights.Single(x => x.Name == request.BaseWeight));
    }

    [Theory]
    [InlineData("detect", "detection", "yolo-detect")]
    [InlineData("detect", "segmentation", "yolo-detect")]
    [InlineData("segment", "segmentation", "yolo-segment")]
    [InlineData("classify", "detection", "classification-crops")]
    public void TrainingFormat_FollowsWeightTask(string task, string dataType, string format)
    {
        Assert.Equal(format, MaskFlowStore.TrainingFormat(task, dataType));
    }

    [Fact]
    public async Task Create_RejectsIncompatibleWeightsBadParametersAndTinyDatasets()
    {
        var (store, owner, project, trainer, _, _) = await SetupAsync();
        async Task<int> Status(TrainingRequest request) =>
            (await Assert.ThrowsAsync<BadHttpRequestException>(() => QueueAsync(store, owner, project, trainer, request))).StatusCode;

        Assert.Equal(400, await Status(new TrainingRequest("yolo11n-seg.pt")));
        Assert.Equal(400, await Status(new TrainingRequest("yolo11n-pose.pt")));
        Assert.Equal(400, await Status(new TrainingRequest("yolo11n.pt", Epochs: 0)));
        Assert.Equal(400, await Status(new TrainingRequest("yolo11n.pt", Batch: 0)));
        Assert.Equal(400, await Status(new TrainingRequest("yolo11n.pt", Split: new SplitConfig(100, 0, 0))));

        var (tiny, tinyOwner, tinyProject, tinyTrainer, _, _) = await SetupAsync(images: 12);
        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() => QueueAsync(tiny, tinyOwner, tinyProject, tinyTrainer));
        Assert.Contains("至少需要 20 张", error.Message);
        Assert.Contains("类别「car」只有 6 张图片", error.Message);
        Assert.Contains("建议再补充约 44 张包含「car」的图片", error.Message);

        await QueueAsync(store, owner, project, trainer);
        Assert.Equal(409, await Status(new TrainingRequest("yolo11n.pt")));
    }

    [Fact]
    public async Task Run_UploadsDatasetFollowsProgressAndKeepsResults()
    {
        var (store, owner, project, trainer, worker, mail) = await SetupAsync();
        var run = await QueueAsync(store, owner, project, trainer);
        var usedBefore = store.GetUser(owner.Id)!.UsedBytes;

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal("training", store.GetTrainingRun(run.Id)!.Status);
        var (fields, dataset) = trainer.Uploads[run.Id];
        Assert.Equal(("yolo11n.pt", "3", "320", "4", "20"), (fields["base_weight"], fields["epochs"], fields["imgsz"], fields["batch"], fields["patience"]));
        using (var zip = new ZipArchive(new MemoryStream(dataset)))
        {
            var names = zip.Entries.Select(x => x.FullName).ToList();
            Assert.Contains("data.yaml", names);
            Assert.Contains(names, x => x.StartsWith("images/train/"));
            Assert.Contains(names, x => x.StartsWith("images/val/"));
            Assert.Equal(24, names.Count(x => x.StartsWith("labels/")));
            using var yaml = new StreamReader(zip.GetEntry("data.yaml")!.Open());
            Assert.Contains("task: detect", yaml.ReadToEnd());
        }

        trainer.Runs[run.Id].History = [new() { ["epoch"] = 1, ["metrics/mAP50(B)"] = 0.1 }, new() { ["epoch"] = 2, ["metrics/mAP50(B)"] = 0.3 }];
        await worker.RunOnceAsync(CancellationToken.None);
        var progress = store.GetTrainingRun(run.Id)!;
        Assert.Equal(("training", 2, 0.3), (progress.Status, progress.Epoch, progress.History[^1]["metrics/mAP50(B)"]));

        var saves = store.State.TrainingRuns.Single();
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Same(saves, store.State.TrainingRuns.Single()); // nothing changed, nothing written

        trainer.Complete(run.Id);
        await worker.RunOnceAsync(CancellationToken.None);

        var done = store.GetTrainingRun(run.Id)!;
        Assert.Equal(("completed", 3, null), (done.Status, done.Epoch, done.Error));
        Assert.Equal(["best.pt", "best.onnx", "results.csv", "confusion_matrix.png", "metrics.json"], done.Artifacts.Select(x => x.Name));
        Assert.All(done.Artifacts, x => Assert.True(File.Exists(x.Path)));
        Assert.Equal(0.41, MaskFlowStore.TrainingHeadline(done)["mAP50-95"]);
        Assert.Equal(0.6, MaskFlowStore.TrainingHeadline(done)["mAP50"]);

        var model = store.State.AiModels.Single(x => x.Id == done.ModelId);
        Assert.Equal((project.Id, 800L), (model.ProjectId, model.Size));
        Assert.Equal(["car", "person"], model.ClassNames!);
        Assert.Equal(usedBefore + done.Artifacts.Sum(x => x.Size) + model.Size, store.GetUser(owner.Id)!.UsedBytes);
        Assert.Contains($"DELETE /api/train/{run.Id}", trainer.Calls);
        Assert.Contains(mail.Sent, x => x.Subject.Contains("训练已完成") && x.Body.Contains("mAP50-95 0.41"));

        // Deleting the run frees its artifacts; the registered model is independent and stays.
        Assert.True(await store.DeleteTrainingRunAsync(project.Id, run.Id));
        Assert.All(done.Artifacts, x => Assert.False(File.Exists(x.Path)));
        Assert.Equal(usedBefore + model.Size, store.GetUser(owner.Id)!.UsedBytes);
        Assert.Contains(store.State.AiModels, x => x.Id == model.Id);
    }

    [Fact]
    public async Task ClassificationRuns_KeepResultsButRegisterNoModel()
    {
        var (store, owner, project, trainer, worker, _) = await SetupAsync();
        var run = await QueueAsync(store, owner, project, trainer, new TrainingRequest("yolo11n-cls.pt"));
        await worker.RunOnceAsync(CancellationToken.None);
        using (var zip = new ZipArchive(new MemoryStream(trainer.Uploads[run.Id].Dataset)))
        {
            Assert.Contains(zip.Entries, x => x.FullName.StartsWith("classification/train/"));
        }

        trainer.Complete(run.Id);
        await worker.RunOnceAsync(CancellationToken.None);
        var done = store.GetTrainingRun(run.Id)!;
        Assert.Equal(("completed", null), (done.Status, done.ModelId));
        Assert.Empty(store.State.AiModels);
    }

    [Fact]
    public async Task FailuresAreReportedAndCleanedUp()
    {
        var (store, owner, project, trainer, worker, mail) = await SetupAsync();
        var run = await QueueAsync(store, owner, project, trainer);
        await worker.RunOnceAsync(CancellationToken.None);
        trainer.Runs[run.Id].Phase = "failed";
        trainer.Runs[run.Id].Error = "OutOfMemoryError: CUDA out of memory";
        trainer.Running = null;

        await worker.RunOnceAsync(CancellationToken.None);

        var failed = store.GetTrainingRun(run.Id)!;
        Assert.Equal(("failed", "OutOfMemoryError: CUDA out of memory"), (failed.Status, failed.Error));
        Assert.NotNull(failed.FinishedAt);
        Assert.DoesNotContain(run.Id, trainer.Runs.Keys);
        Assert.Contains(mail.Sent, x => x.Subject.Contains("训练失败"));
    }

    [Fact]
    public async Task UnreachableTrainer_KeepsTheRunAndExplains_LostRunsFail()
    {
        var (store, owner, project, trainer, worker, _) = await SetupAsync();
        var run = await QueueAsync(store, owner, project, trainer);
        await worker.RunOnceAsync(CancellationToken.None);

        trainer.Down = true;
        await worker.RunOnceAsync(CancellationToken.None);
        var stale = store.GetTrainingRun(run.Id)!;
        Assert.Equal("training", stale.Status);
        Assert.Contains("训练服务不可用", stale.Error);

        trainer.Down = false;
        trainer.Runs.Remove(run.Id);
        trainer.Running = null;
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal("failed", store.GetTrainingRun(run.Id)!.Status);
        Assert.Contains("找不到", store.GetTrainingRun(run.Id)!.Error);
    }

    [Fact]
    public async Task Cancel_StopsTheRemoteRunAndTheNextRunStarts()
    {
        var (store, owner, project, trainer, worker, _) = await SetupAsync();
        var first = await QueueAsync(store, owner, project, trainer);
        await worker.RunOnceAsync(CancellationToken.None);

        var cancelled = await worker.CancelAsync(first.Id, CancellationToken.None);
        Assert.Equal("cancelled", cancelled!.Status);
        Assert.Contains($"POST /api/train/{first.Id}/cancel", trainer.Calls);
        Assert.Null(trainer.Running);
        Assert.Null(await worker.CancelAsync(first.Id, CancellationToken.None));

        var second = await QueueAsync(store, owner, project, trainer);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal("training", store.GetTrainingRun(second.Id)!.Status);
        Assert.Equal("cancelled", store.GetTrainingRun(first.Id)!.Status);
    }

    [Fact]
    public async Task OrphanedRemoteRuns_AreStoppedBeforeStarting()
    {
        var (store, owner, project, trainer, worker, _) = await SetupAsync();
        trainer.Runs["train_orphan"] = new FakeTrainer.Run();
        trainer.Running = "train_orphan";
        var run = await QueueAsync(store, owner, project, trainer);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal("queued", store.GetTrainingRun(run.Id)!.Status);
        Assert.Contains("POST /api/train/train_orphan/cancel", trainer.Calls);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal("training", store.GetTrainingRun(run.Id)!.Status);
    }

    [Fact]
    public async Task StorageQuota_FailsTheRunWithoutLeavingFiles()
    {
        var (store, owner, project, trainer, worker, _) = await SetupAsync();
        var run = await QueueAsync(store, owner, project, trainer);
        await worker.RunOnceAsync(CancellationToken.None);
        trainer.Complete(run.Id);
        await store.MutateAsync(state =>
        {
            var index = state.Users.FindIndex(x => x.Id == owner.Id);
            state.Users[index] = state.Users[index] with { QuotaBytes = state.Users[index].UsedBytes + 100 };
            return true;
        });

        await worker.RunOnceAsync(CancellationToken.None);

        var failed = store.GetTrainingRun(run.Id)!;
        Assert.Equal("failed", failed.Status);
        Assert.Contains("存储空间不足", failed.Error);
        Assert.Empty(failed.Artifacts);
        var trainingDir = Path.Combine(store.StorageRoot, owner.Id.ToString(), "trainings", run.Id);
        Assert.True(!Directory.Exists(trainingDir) || Directory.GetFiles(trainingDir).Length == 0);
    }

    [Fact]
    public async Task ActiveTraining_BlocksDeletes()
    {
        var (store, owner, project, trainer, worker, _) = await SetupAsync();
        var run = await QueueAsync(store, owner, project, trainer);
        Assert.Equal(409, (await Assert.ThrowsAsync<BadHttpRequestException>(() => store.DeleteTrainingRunAsync(project.Id, run.Id))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<BadHttpRequestException>(() => store.DeleteProjectAsync(owner.Id, project.Id))).StatusCode);

        await worker.CancelAsync(run.Id, CancellationToken.None);
        Assert.True(await store.DeleteProjectAsync(owner.Id, project.Id));
        Assert.Empty(store.State.TrainingRuns);
    }

    [Fact]
    public async Task InterruptedUploads_AreRequeued()
    {
        var (store, owner, project, trainer, _, _) = await SetupAsync();
        var run = await QueueAsync(store, owner, project, trainer);
        await store.UpdateTrainingRunAsync(run.Id, x => x with { Status = "preparing", StartedAt = DateTimeOffset.UtcNow });

        await store.RequeueInterruptedTrainingsAsync();

        Assert.Equal(("queued", (DateTimeOffset?)null), (store.GetTrainingRun(run.Id)!.Status, store.GetTrainingRun(run.Id)!.StartedAt));
    }
}
