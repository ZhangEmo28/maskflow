using Microsoft.AspNetCore.Http;

namespace MaskFlow.Api.Tests;

sealed class FakeRepository : IMaskFlowRepository
{
    public int Saves { get; private set; }
    public bool FailNextSave { get; set; }

    public MaskFlowState? Initial { get; set; }
    public List<(MaskFlowState? Previous, MaskFlowState Current)> Writes { get; } = [];

    public Task EnsureSchemaAsync() => Task.CompletedTask;
    public Task<MaskFlowLoadResult> LoadAsync() => Task.FromResult(new MaskFlowLoadResult(Initial, Initial is null));

    public Task SaveChangesAsync(MaskFlowState? previous, MaskFlowState current)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new InvalidOperationException("database unavailable");
        }

        Saves++;
        Writes.Add((previous, current));
        return Task.CompletedTask;
    }
}

static class TestStore
{
    public static (MaskFlowStore Store, FakeRepository Repository) Create(MaskFlowState? initial = null)
    {
        var repository = new FakeRepository { Initial = initial };
        var store = new MaskFlowStore(repository, Path.Combine(Path.GetTempPath(), "maskflow-store-" + Guid.NewGuid().ToString("N")));
        store.Initialize();
        return (store, repository);
    }

    public static async Task<User> RegisterAsync(MaskFlowStore store, string email = "owner@example.com")
    {
        var result = await store.RegisterAsync(email, "correct-horse-battery", "owner", new DefaultHttpContext());
        return store.GetUser(result!.User.Id)!;
    }

    public static async Task<FileItem> AddFileAsync(MaskFlowStore store, int userId, string projectId, string path = "/tmp/maskflow-test-missing.jpg")
    {
        return await store.MutateAsync(state =>
        {
            var file = new FileItem(state.NextFileId++, userId, projectId, "a.jpg", path, 10, "image", "image/jpeg", DateTimeOffset.UtcNow, "/api/files/1/download");
            state.Files.Add(file);
            return file;
        });
    }
}

public class MaskFlowStoreTests
{
    [Fact]
    public async Task FailedLogin_DoesNotWriteDatabase()
    {
        var (store, repository) = TestStore.Create();
        await TestStore.RegisterAsync(store);
        var saves = repository.Saves;

        Assert.Null(await store.LoginAsync("owner@example.com", "wrong-password", new DefaultHttpContext()));
        Assert.Null(await store.LoginAsync("nobody@example.com", "whatever-password", new DefaultHttpContext()));

        Assert.Equal(saves, repository.Saves);
        Assert.NotNull(await store.LoginAsync("owner@example.com", "correct-horse-battery", new DefaultHttpContext()));
    }

    [Fact]
    public async Task ChangePassword_KeepsConcurrentUserChanges()
    {
        var (store, _) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        await store.MutateAsync(state =>
        {
            var current = state.Users.Single(x => x.Id == user.Id);
            state.Users.Remove(current);
            state.Users.Add(current with { UsedBytes = 1234 });
            return 0;
        });

        Assert.True(await store.ChangePasswordAsync(user.Id, "correct-horse-battery", "new-password-123"));

        var stored = Assert.Single(store.State.Users, x => x.Id == user.Id);
        Assert.Equal(1234, stored.UsedBytes);
        Assert.NotNull(await store.LoginAsync(user.Email, "new-password-123", new DefaultHttpContext()));
    }

    [Fact]
    public async Task AutoAnnotation_KeepsConfirmedTargetsAndCompletesJob()
    {
        var (store, _) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(user.Id, new ProjectCreate("p", null, "detection", null));
        await store.SaveProjectLabelsAsync(user.Id, project.Id, ["car"]);
        var file = await TestStore.AddFileAsync(store, user.Id, project.Id);
        var confirmed = new AnnotationItem("human", 0, "car", new YoloBox(0.2, 0.2, 0.2, 0.2), null, 1, true);
        await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(file.Id, 100, 100,
        [
            confirmed,
            new AnnotationItem("stale-ai", 0, "car", new YoloBox(0.8, 0.8, 0.1, 0.1), null, 0.5, false)
        ]));

        var (set, task) = await store.PersistAutoAnnotationAsync(user.Id, new AnnotationSaveRequest(file.Id, 100, 100,
        [
            new AnnotationItem("ann_dup", 0, "car", new YoloBox(0.21, 0.2, 0.2, 0.2), null, 0.9, false),
            new AnnotationItem("ann_new", 0, "car", new YoloBox(0.5, 0.5, 0.1, 0.1), null, 0.9, false)
        ]), file);

        Assert.Equal(["human", "ann_new"], set.Annotations.Select(x => x.Id));
        Assert.Equal("completed", task!.Status);
        Assert.Equal("succeeded", store.State.Jobs.Single(x => x.Id == task.JobId).Status);
    }

    [Fact]
    public async Task PollJob_OnlyServesApprovedNodesFromTheirPool()
    {
        var (store, _) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        await store.CreateTaskAsync(user.Id, "auto_segment", null, null, null, 1);
        var gpuJob = await store.AddJobAsync(new JobCreate("train", user.Id, null, [], []));
        var node = await store.RegisterNodeAsync(new NodeRegister(Pool: "platform-gpu"), "mf_key");

        var (found, pending) = await store.PollJobAsync(node.Id);
        Assert.True(found);
        Assert.Null(pending);

        await store.NodeStatusAsync(node.Id, "online", approve: true);
        var (_, job) = await store.PollJobAsync(node.Id);
        Assert.Equal(gpuJob.Id, job!.Id);

        var (_, next) = await store.PollJobAsync(node.Id);
        Assert.Null(next);
    }

    [Fact]
    public async Task DeleteFile_KeepsObjectWhenDatabaseWriteFails()
    {
        var (store, repository) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(user.Id, new ProjectCreate("p", null, null, null));
        var path = Path.Combine(Path.GetTempPath(), $"maskflow-test-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            var file = await TestStore.AddFileAsync(store, user.Id, project.Id, path);

            repository.FailNextSave = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFileAsync(user.Id, file.Id));
            Assert.True(File.Exists(path));
            Assert.Contains(store.State.Files, x => x.Id == file.Id);

            Assert.True(await store.DeleteFileAsync(user.Id, file.Id));
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task QuotaRead_DoesNotMutateState()
    {
        var (store, _) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        await store.MutateAsync(state =>
        {
            state.Quotas[user.Id.ToString()] = new AiQuota("free", 50, 49, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
            return 0;
        });

        var quota = store.GetQuota(user);

        Assert.Equal(0, quota.DailyUsed);
        Assert.Equal(49, store.State.Quotas[user.Id.ToString()].DailyUsed);
    }
}

public class MaskFlowStoreConcurrencyTests
{
    [Fact]
    public async Task Readers_NeverSeeUncommittedWrites()
    {
        var (store, _) = TestStore.Create();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var write = store.SaveAsync(async () =>
        {
            store.State.Pools.Add(new Pool("pending", "Pending", "gpu", null, "active", [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            entered.SetResult();
            await release.Task;
        });

        await entered.Task;
        // A different async flow (another request) still sees the committed snapshot.
        var visibleDuringWrite = await Task.Run(() => store.State.Pools.Any(x => x.Id == "pending"));
        release.SetResult();
        await write;

        Assert.False(visibleDuringWrite);
        Assert.Contains(store.State.Pools, x => x.Id == "pending");
    }

    [Fact]
    public async Task FailedWrites_AreNeverPublished()
    {
        var (store, repository) = TestStore.Create();
        repository.FailNextSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(() =>
            store.State.Pools.Add(new Pool("ghost", "Ghost", "gpu", null, "active", [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))));

        Assert.DoesNotContain(store.State.Pools, x => x.Id == "ghost");
    }

    [Fact]
    public async Task Writes_PassOnlyTheChangedRowsToTheRepository()
    {
        var (store, repository) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(user.Id, new ProjectCreate("p", null, null, null));
        await TestStore.AddFileAsync(store, user.Id, project.Id);

        await store.UpdateProjectAsync(user.Id, project.Id, new ProjectCreate("renamed", null, null, null));

        var (previous, current) = repository.Writes[^1];
        Assert.Single(StateDiff.Compare(previous!.Projects, current.Projects, x => x.Id).Upserts);
        Assert.True(StateDiff.Compare(previous.Files, current.Files, x => x.Id).IsEmpty);
        Assert.True(StateDiff.Compare(previous.Users, current.Users, x => x.Id).IsEmpty);
    }

    [Fact]
    public async Task Sessions_StoreOnlyTokenHashes()
    {
        var (store, _) = TestStore.Create();
        var result = await store.RegisterAsync("hash@example.com", "correct-horse-battery", null, new DefaultHttpContext());

        Assert.DoesNotContain(store.State.Sessions, x => x.Token == result!.Token);
        Assert.NotNull(store.OptionalUser(WithBearer(result!.Token)));
        Assert.Null(store.OptionalUser(WithBearer(store.State.Sessions[0].Token)));
    }

    [Fact]
    public void LegacyPlaintextSessions_AreHashedOnStartup()
    {
        var legacy = new MaskFlowState();
        legacy.Users.Add(new User(1, "old@example.com", "x", "00", "free", 1, 0, DateTimeOffset.UtcNow, null, null, null));
        legacy.Sessions.Add(new Session("plain-token-from-before", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));

        var (store, _) = TestStore.Create(legacy);

        Assert.Equal(Util.Sha256("plain-token-from-before"), store.State.Sessions[0].Token);
        Assert.Equal(1, store.OptionalUser(WithBearer("plain-token-from-before"))!.Id);
    }

    static HttpContext WithBearer(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }
}

public class ProjectCopyTests
{
    [Fact]
    public async Task CopyProject_CopiesFilesAndRemovesCopiesWhenCommitFails()
    {
        var (store, repository) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(user.Id, new ProjectCreate("src", null, "detection", null));
        await store.SaveProjectLabelsAsync(user.Id, project.Id, ["car"]);
        var path = Path.Combine(Path.GetTempPath(), $"maskflow-copy-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [9, 9, 9]);
        try
        {
            var file = await TestStore.AddFileAsync(store, user.Id, project.Id, path);
            await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(file.Id, 10, 10,
                [new AnnotationItem("a", 0, "car", new YoloBox(0.5, 0.5, 0.2, 0.2), null, 1, true)]));

            // Other test classes write exports under the same storage root in parallel; only look at project copies.
            HashSet<string> ProjectCopies() => Directory.Exists(store.StorageRoot)
                ? Directory.EnumerateFiles(store.StorageRoot, "*", SearchOption.AllDirectories)
                    .Where(x => x.Contains($"{Path.DirectorySeparatorChar}projects{Path.DirectorySeparatorChar}"))
                    .ToHashSet()
                : [];
            var copiesBefore = ProjectCopies();
            repository.FailNextSave = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CopyProjectAsync(user.Id, project.Id, new ProjectCopyRequest("copy")));
            Assert.Single(store.State.Projects);
            Assert.Equal(copiesBefore, ProjectCopies());

            var copied = await store.CopyProjectAsync(user.Id, project.Id, new ProjectCopyRequest("copy"));
            var copiedFile = Assert.Single(store.State.Files, x => x.ProjectId == copied!.Id);
            Assert.NotEqual(path, copiedFile.Path);
            Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(copiedFile.Path));
            Assert.Equal(["car"], store.State.ProjectLabels[copied!.Id]);
            Assert.Equal("car", Assert.Single(store.State.AnnotationSets, x => x.FileId == copiedFile.Id).Annotations[0].Label);
            File.Delete(copiedFile.Path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
