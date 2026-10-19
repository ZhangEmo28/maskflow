using Microsoft.AspNetCore.Http;
using MySqlConnector;

namespace MaskFlow.Api.Tests;

// Runs against a real MySQL when MASKFLOW_TEST_MYSQL is set, e.g.
// "Server=127.0.0.1;Port=3306;User ID=root;Password=...;" (a throwaway database is created and dropped).
public class MySqlRepositoryIntegrationTests
{
    static string? BaseConnectionString => Environment.GetEnvironmentVariable("MASKFLOW_TEST_MYSQL");

    [Fact]
    public async Task IncrementalWrites_RoundTripThroughMySql()
    {
        if (string.IsNullOrWhiteSpace(BaseConnectionString))
        {
            return;
        }

        var database = "maskflow_it_" + Guid.NewGuid().ToString("N")[..12];
        var builder = new MySqlConnectionStringBuilder(BaseConnectionString) { Database = database };
        try
        {
            var store = new MaskFlowStore(new MySqlMaskFlowRepository(builder.ConnectionString));
            store.Initialize();
            var auth = await store.RegisterAsync("it@example.com", "correct-horse-battery", "it", new DefaultHttpContext());
            var userId = auth!.User.Id;
            var project = await store.CreateProjectAsync(userId, new ProjectCreate("项目", "desc", "segmentation", null));
            await store.SaveProjectLabelsAsync(userId, project.Id, ["car", "安全帽"]);
            var files = new List<FileItem>();
            for (var i = 0; i < 450; i++)
            {
                files.Add(await TestStore.AddFileAsync(store, userId, project.Id, $"/tmp/it-{i}.jpg"));
            }

            await store.SaveAnnotationSetAsync(userId, new AnnotationSaveRequest(files[0].Id, 640, 480,
                [new AnnotationItem("a1", 1, "安全帽", new YoloBox(0.5, 0.5, 0.2, 0.2), [0.4, 0.4, 0.6, 0.4, 0.6, 0.6], 0.9, true)]));
            await store.SaveProjectLabelsAsync(userId, project.Id, ["安全帽", "car", "person"]);
            await store.SaveLabelPromptsAsync(userId, project.Id, new Dictionary<string, string?> { ["安全帽"] = "hard hat" });
            await store.DeleteFileAsync(userId, files[1].Id);
            var weights = new byte[] { 1, 2, 3, 4 };
            await using var weightStream = new MemoryStream(weights);
            var model = await store.AddAiModelAsync(store.GetUser(userId)!, new FormFile(weightStream, 0, weights.Length, "file", "helmet.onnx"), null, project.Id);
            await store.UpdateAiModelClassNamesAsync(model.Id, ["hard hat"]);

            var reloaded = new MaskFlowStore(new MySqlMaskFlowRepository(builder.ConnectionString));
            reloaded.Initialize();
            var state = reloaded.State;

            Assert.Equal(449, state.Files.Count(x => x.ProjectId == project.Id));
            Assert.DoesNotContain(state.Files, x => x.Id == files[1].Id);
            Assert.Equal(["安全帽", "car", "person"], state.ProjectLabels[project.Id]);
            var set = Assert.Single(state.AnnotationSets);
            Assert.Equal("安全帽", set.Annotations[0].Label);
            Assert.True(set.Annotations[0].Confirmed);
            Assert.NotNull(reloaded.OptionalUser(WithBearer(auth.Token)));
            Assert.Equal(store.State.NextFileId, state.NextFileId);
            Assert.Contains(state.Pools, x => x.Id == "platform-gpu");
            Assert.Equal("hard hat", reloaded.PromptForLabel(project.Id, "安全帽"));
            var reloadedModel = Assert.Single(state.AiModels);
            Assert.Equal(["hard hat"], reloadedModel.ClassNames);
            Assert.Equal(model.Sha256, reloadedModel.Sha256);
            File.Delete(model.Path);
        }
        finally
        {
            await using var connection = new MySqlConnection(BaseConnectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS `{database}`";
            await drop.ExecuteNonQueryAsync();
        }
    }

    static HttpContext WithBearer(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }
}
