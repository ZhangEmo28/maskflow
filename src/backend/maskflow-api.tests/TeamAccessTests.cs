using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MaskFlow.Api.Tests;

// Boots the real API (routing, filters, controllers) on an in-memory repository.
public class MaskFlowApp : WebApplicationFactory<Program>
{
    public FakeRepository Repository { get; } = new();
    public FakeEmailSender Mail { get; } = new();
    public string Storage { get; } = Path.Combine(Path.GetTempPath(), "maskflow-app-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMaskFlowRepository>();
            services.AddSingleton<IMaskFlowRepository>(Repository);
            services.RemoveAll<MaskFlowStore>();
            services.AddSingleton(new MaskFlowStore(Repository, Storage));
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Mail);
            ConfigureFakes(services);
        });
    }

    // Subclasses swap in fake inference/training services.
    protected virtual void ConfigureFakes(IServiceCollection services) { }

    public async Task<Client> SignUpAsync(string name)
    {
        var client = CreateClient();
        var email = $"{name}-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "correct-horse-battery", username = name });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return new Client(client, body.GetProperty("user").GetProperty("id").GetInt32(), email);
    }
}

public sealed record Client(HttpClient Http, int Id, string Email)
{
    public async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public Task<HttpResponseMessage> Get(string url) => Http.GetAsync(url);
    public Task<HttpResponseMessage> Post(string url, object? body = null) => Http.PostAsJsonAsync(url, body ?? new { });
    public Task<HttpResponseMessage> Put(string url, object body) => Http.PutAsJsonAsync(url, body);
    public Task<HttpResponseMessage> Delete(string url) => Http.DeleteAsync(url);

    public async Task<int> UploadAsync(string projectId)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(projectId), "projectId");
        var image = new ByteArrayContent(ImageFixture.Png());
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "file", $"img-{Guid.NewGuid():N}.png");
        var response = await Http.PostAsync("/api/files/upload", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).GetProperty("files")[0].GetProperty("id").GetInt32();
    }
}

public class TeamAccessTests : IClassFixture<MaskFlowApp>
{
    readonly MaskFlowApp app;

    public TeamAccessTests(MaskFlowApp app) => this.app = app;

    static object Annotation(int fileId, bool confirmed) => new
    {
        fileId,
        width = 64,
        height = 48,
        annotations = new[] { new { id = "a1", classId = 0, label = "car", bbox = new { cx = 0.5, cy = 0.5, width = 0.2, height = 0.2 }, confidence = 1.0, confirmed } }
    };

    async Task<(Client Owner, string ProjectId, int FileId)> ProjectWithImageAsync()
    {
        var owner = await app.SignUpAsync("owner");
        var created = await owner.Json(await owner.Post("/api/projects", new { name = "shared", dataType = "detection" }));
        var projectId = created.GetProperty("project").GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await owner.Put($"/api/projects/{projectId}/labels", new { labels = new[] { "car" } })).StatusCode);
        var fileId = await owner.UploadAsync(projectId);
        Assert.Equal(HttpStatusCode.OK, (await owner.Put($"/api/annotations/file/{fileId}", Annotation(fileId, true))).StatusCode);
        return (owner, projectId, fileId);
    }

    async Task<Client> AddMemberAsync(Client owner, string projectId, string role)
    {
        var member = await app.SignUpAsync(role);
        var response = await owner.Post($"/api/projects/{projectId}/members", new { email = member.Email, role });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return member;
    }

    [Fact]
    public async Task Strangers_GetNotFoundEverywhere()
    {
        var (owner, projectId, fileId) = await ProjectWithImageAsync();
        var stranger = await app.SignUpAsync("stranger");
        var exportBody = new { projectId, format = "yolo-detect", split = new { train = 100, val = 0, test = 0 } };

        foreach (var response in new[]
        {
            await stranger.Get($"/api/projects/{projectId}"),
            await stranger.Get($"/api/projects/{projectId}/labels"),
            await stranger.Get($"/api/projects/{projectId}/members"),
            await stranger.Get($"/api/files?projectId={projectId}"),
            await stranger.Get($"/api/files/{fileId}/download"),
            await stranger.Get($"/api/annotations/file/{fileId}"),
            await stranger.Put($"/api/annotations/file/{fileId}", Annotation(fileId, false)),
            await stranger.Post($"/api/annotations/file/{fileId}/approve"),
            await stranger.Delete($"/api/files/{fileId}"),
            await stranger.Post("/api/export/analyze", exportBody),
            await stranger.Post("/api/export/dataset", exportBody),
            await stranger.Post("/api/annotations/batch", new { projectId }),
            await stranger.Post($"/api/annotations/file/{fileId}/propagate", new { count = 3 }),
            await stranger.Get($"/api/projects/{projectId}/videos"),
            await stranger.Delete($"/api/projects/{projectId}")
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Post($"/api/annotations/file/{fileId}/propagate", new { count = 3 })).StatusCode);

        var projects = await stranger.Json(await stranger.Get("/api/projects"));
        Assert.Equal(0, projects.GetProperty("projects").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await owner.Get($"/api/files/{fileId}/download")).StatusCode);
    }

    [Fact]
    public async Task Viewer_CanReadButNotChangeAnything()
    {
        var (owner, projectId, fileId) = await ProjectWithImageAsync();
        var viewer = await AddMemberAsync(owner, projectId, "viewer");

        var list = await viewer.Json(await viewer.Get("/api/projects"));
        var shared = list.GetProperty("projects")[0];
        Assert.Equal(("viewer", 1), (shared.GetProperty("role").GetString(), shared.GetProperty("imageCount").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get($"/api/files/{fileId}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get($"/api/annotations/file/{fileId}")).StatusCode);
        Assert.Equal(1, (await viewer.Json(await viewer.Get($"/api/files?projectId={projectId}"))).GetProperty("files").GetArrayLength());

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Put($"/api/annotations/file/{fileId}", Annotation(fileId, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Put($"/api/projects/{projectId}/labels", new { labels = new[] { "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post("/api/export/dataset", new { projectId, format = "yolo-detect", split = new { train = 100, val = 0, test = 0 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Delete($"/api/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/projects/{projectId}/members", new { email = "x@example.com", role = "viewer" })).StatusCode);
    }

    [Fact]
    public async Task Annotator_SubmitsAndReviewerApprovesOrRejects()
    {
        var (owner, projectId, fileId) = await ProjectWithImageAsync();
        var annotator = await AddMemberAsync(owner, projectId, "annotator");
        var reviewer = await AddMemberAsync(owner, projectId, "reviewer");

        // Annotator edits never count as human confirmation.
        var saved = await annotator.Json(await annotator.Put($"/api/annotations/file/{fileId}", Annotation(fileId, true)));
        Assert.False(saved.GetProperty("annotation").GetProperty("annotations")[0].GetProperty("confirmed").GetBoolean());
        Assert.Equal("draft", saved.GetProperty("annotation").GetProperty("reviewStatus").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await annotator.Post($"/api/annotations/file/{fileId}/approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await annotator.Delete($"/api/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await annotator.Put($"/api/projects/{projectId}/labels", new { labels = new[] { "x" } })).StatusCode);
        var uploaded = await annotator.UploadAsync(projectId);

        var submitted = await annotator.Json(await annotator.Post($"/api/annotations/file/{fileId}/submit"));
        Assert.Equal("submitted", submitted.GetProperty("annotation").GetProperty("reviewStatus").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.Post($"/api/annotations/file/{fileId}/reject", new { comment = " " })).StatusCode);
        var rejected = await reviewer.Json(await reviewer.Post($"/api/annotations/file/{fileId}/reject", new { comment = "框太大" }));
        Assert.Equal(("rejected", "框太大"), (rejected.GetProperty("annotation").GetProperty("reviewStatus").GetString(), rejected.GetProperty("annotation").GetProperty("reviewComment").GetString()));

        var approved = await reviewer.Json(await reviewer.Post($"/api/annotations/file/{fileId}/approve"));
        Assert.Equal("approved", approved.GetProperty("annotation").GetProperty("reviewStatus").GetString());
        Assert.True(approved.GetProperty("annotation").GetProperty("annotations")[0].GetProperty("confirmed").GetBoolean());

        // Data and quota stay with the owner.
        var store = app.Services.GetRequiredService<MaskFlowStore>();
        Assert.Equal(owner.Id, store.State.Files.Single(x => x.Id == uploaded).UserId);
        Assert.Equal(HttpStatusCode.OK, (await reviewer.Post("/api/export/analyze", new { projectId, format = "yolo-detect", split = new { train = 100, val = 0, test = 0 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.Post($"/api/projects/{projectId}/members", new { email = "y@example.com", role = "viewer" })).StatusCode);
    }

    [Fact]
    public async Task RemovedMembersLoseAccessAndAssignmentsAreReleased()
    {
        var (owner, projectId, fileId) = await ProjectWithImageAsync();
        var annotator = await AddMemberAsync(owner, projectId, "annotator");
        var second = await owner.UploadAsync(projectId);

        var assigned = await owner.Json(await owner.Post($"/api/projects/{projectId}/assignments", new { userIds = new[] { annotator.Id, owner.Id }, scope = "all" }));
        Assert.Equal(2, assigned.GetProperty("changed").GetInt32());
        var files = (await annotator.Json(await annotator.Get($"/api/files?projectId={projectId}"))).GetProperty("files").EnumerateArray().ToList();
        Assert.Contains(files, x => x.GetProperty("assignedTo").GetInt32() == annotator.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Post($"/api/projects/{projectId}/assignments", new { userIds = new[] { 999999 } })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.Delete($"/api/projects/{projectId}/members/{annotator.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await annotator.Get($"/api/files/{fileId}/download")).StatusCode);
        var store = app.Services.GetRequiredService<MaskFlowStore>();
        Assert.DoesNotContain(store.State.FileAssignments, x => x.UserId == annotator.Id);
        Assert.Contains(store.State.FileAssignments, x => x.FileId == second || x.FileId == fileId);
    }

    [Fact]
    public async Task InvitesForNewEmails_RequireTheTokenAndMatchingAccount()
    {
        var (owner, projectId, fileId) = await ProjectWithImageAsync();
        var email = $"newcomer-{Guid.NewGuid():N}@example.com";

        var invited = await owner.Json(await owner.Post($"/api/projects/{projectId}/members", new { email, role = "annotator" }));
        var url = invited.GetProperty("inviteUrl").GetString()!;
        var token = url[(url.IndexOf("invite=", StringComparison.Ordinal) + "invite=".Length)..];
        Assert.Contains(app.Mail.Sent, x => x.To == email && x.Body.Contains("注册"));

        // Registering the invited email alone grants nothing.
        var client = app.CreateClient();
        var registered = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "correct-horse-battery" });
        var body = await registered.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        var newcomer = new Client(client, body.GetProperty("user").GetProperty("id").GetInt32(), email);
        Assert.Equal(HttpStatusCode.NotFound, (await newcomer.Get($"/api/annotations/file/{fileId}")).StatusCode);

        var someoneElse = await app.SignUpAsync("other");
        Assert.Equal(HttpStatusCode.Forbidden, (await someoneElse.Post($"/api/invites/{token}/accept")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await newcomer.Post($"/api/invites/{token}/accept")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await newcomer.Get($"/api/annotations/file/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await newcomer.Post($"/api/invites/{token}/accept")).StatusCode);
    }

    [Fact]
    public async Task OwnerCanChangeRolesAndMembersCanLeave()
    {
        var (owner, projectId, fileId) = await ProjectWithImageAsync();
        var member = await AddMemberAsync(owner, projectId, "viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Put($"/api/annotations/file/{fileId}", Annotation(fileId, false))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.Put($"/api/projects/{projectId}/members/{member.Id}", new { role = "reviewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.Put($"/api/annotations/file/{fileId}", Annotation(fileId, true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Put($"/api/projects/{projectId}/members/{member.Id}", new { role = "owner" })).StatusCode);

        var members = await owner.Json(await owner.Get($"/api/projects/{projectId}/members"));
        Assert.Equal("reviewer", members.GetProperty("members")[0].GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.OK, (await member.Delete($"/api/projects/{projectId}/members/{member.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Get($"/api/projects/{projectId}")).StatusCode);
    }
}
