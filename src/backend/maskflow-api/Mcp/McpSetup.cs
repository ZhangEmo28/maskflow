using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// MCP (Model Context Protocol) endpoint so agents such as Claude and ChatGPT can drive MaskFlow
// from a chat: Streamable HTTP at /mcp, authenticated with the same Bearer tokens as the REST API
// (an API token from 账户设置, or an OAuth access token issued to a connector).
public static class McpSetup
{
    public const string Path = "/mcp";

    const string Instructions = """
        MaskFlow is an image annotation and model training platform. The user uploads images in the web app;
        you help label them, check quality, train YOLO models and evaluate them. Reply in the user's language.

        Typical workflow:
        1. list_projects / get_project to find the project (or create_project). Labels may be Chinese; for SAM 3
           text detection give each label an English prompt with update_labels (e.g. 安全帽 -> "hard hat").
        2. auto_annotate_image on one or two images, then view_image to check the result visually before running
           auto_annotate_project on the rest (a background task: poll get_task). Images where AI detects nothing stay
           "unannotated" (not saved); retry with a lower conf or another mode, or add targets with edit_annotations.
        3. check_quality, then view_image the flagged images and fix them with edit_annotations (coordinates are
           normalized 0-1, box = [x1, y1, x2, y2]). Targets you add or change stay unconfirmed; only call
           confirm_annotations after the user agrees, because "confirmed" means a human approved it.
        4. check_training_data first: it blocks too-small datasets and tells the user how many more images of which
           class (and what kind) to add. Then list_base_weights and start_training. Poll get_training; when done,
           report validation/test mAP and per-class results, and view_training_plot for the confusion matrix.
        5. evaluate_model compares a model's predictions with human-confirmed labels and lists the worst images.
        AI runs (auto annotation, model predictions in view_image/evaluate_model) consume the project owner's daily AI quota.
        Files and downloads (weights, datasets) are fetched by the user from the web app; give them the page link.
        """;

    public static IServiceCollection AddMaskFlowMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<McpCaller>();
        services.AddSingleton<OAuthCodeStore>();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "maskflow", Title = "MaskFlow", Version = "2.2" };
                options.ServerInstructions = Instructions;
            })
            // Stateless: every request is self-contained, so any API instance can serve it and restarts lose nothing.
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<ProjectTools>()
            .WithTools<AnnotationTools>()
            .WithTools<TrainingTools>();
        return services;
    }

    public static WebApplication MapMaskFlowMcp(this WebApplication app)
    {
        app.UseWhen(context => context.Request.Path.StartsWithSegments(Path), branch => branch.Use(async (context, next) =>
        {
            var store = context.RequestServices.GetRequiredService<MaskFlowStore>();
            if (store.OptionalUser(context) is null)
            {
                // Points OAuth-capable clients (ChatGPT, claude.ai) at the protected resource metadata.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate =
                    $"Bearer realm=\"MaskFlow\", resource_metadata=\"{OAuthEndpoints.PublicBase(context)}/.well-known/oauth-protected-resource\"";
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized", detail = "需要 MaskFlow API 令牌（账户设置 → API 令牌）或 OAuth 授权。" });
                return;
            }

            await next();
        }));
        app.MapMcp(Path);
        return app;
    }
}

// The signed-in caller of an MCP request, with the same access rules as the REST controllers.
public sealed class McpCaller(IHttpContextAccessor accessor, MaskFlowStore store)
{
    public User User => store.OptionalUser(accessor.HttpContext ?? throw new McpException("没有 HTTP 上下文。"))
                        ?? throw new McpException("未登录或令牌已失效。");

    public int UserId => User.Id;

    public CancellationToken Aborted => accessor.HttpContext?.RequestAborted ?? CancellationToken.None;

    public ProjectAccess Project(string projectId, Func<ProjectAccess, bool> permission) =>
        store.RequireProjectAccess(UserId, projectId, permission);

    public (FileItem File, int OwnerId, ProjectAccess? Access) File(int fileId, Func<ProjectAccess, bool> permission) =>
        store.RequireFileAccess(UserId, fileId, permission);

    public TaskItem Task(string taskId)
    {
        var task = store.State.Tasks.FirstOrDefault(x => x.Id == taskId) ?? throw new McpException("任务不存在。");
        if (task.UserId == UserId) return task;
        if (task.ProjectId is not null && store.GetProjectAccess(UserId, task.ProjectId) is not null) return task;
        throw new McpException("任务不存在。");
    }

    public TrainingRun Training(string trainingId, Func<ProjectAccess, bool> permission)
    {
        var run = store.GetTrainingRun(trainingId) ?? throw new McpException("训练不存在。");
        store.RequireProjectAccess(UserId, run.ProjectId, permission);
        return run;
    }

    // Errors the model should see (validation, permissions, service problems) become tool errors with
    // the original message; anything else stays a generic failure so internals are not leaked.
    public static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (Translate(ex) is { } message)
        {
            throw new McpException(message);
        }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (Translate(ex) is { } message)
        {
            throw new McpException(message);
        }
    }

    static string? Translate(Exception ex) => ex switch
    {
        McpException mcp => mcp.Message,
        BadHttpRequestException bad => bad.StatusCode switch
        {
            404 => $"找不到：{bad.Message}",
            403 => $"没有权限：{bad.Message}",
            _ => bad.Message
        },
        SamServiceException sam => $"推理服务返回错误：{sam.Detail}",
        HttpRequestException http => $"推理服务不可用：{http.Message}",
        TaskCanceledException => "推理服务超时，请稍后重试。",
        _ => null
    };

    public static string PublicUrl => (Environment.GetEnvironmentVariable("MASKFLOW_PUBLIC_URL") ?? "http://127.0.0.1:3000").TrimEnd('/');
}
