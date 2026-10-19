using Microsoft.AspNetCore.Mvc;

[Route("api/tasks")]
[Tags("Tasks")]
public sealed class TasksController : MaskFlowControllerBase
{
    public TasksController(MaskFlowStore store) : base(store) { }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] TaskCreate request)
    {
        var user = CurrentUser();
        var task = await Store.CreateTaskAsync(user.Id, request.Type, request.Title, request.ProjectId, request.FileId, request.ImageCount);
        return Ok(new { task });
    }

    // Users see their own tasks plus tasks of projects shared with them (e.g. a teammate's batch run).
    [HttpGet]
    public IActionResult List()
    {
        var user = CurrentUser();
        var projects = Store.AccessibleProjects(user.Id).Select(x => x.Project.Id).ToHashSet();
        return Ok(new { tasks = Store.State.Tasks.Where(x => x.UserId == user.Id || (x.ProjectId is not null && projects.Contains(x.ProjectId))).OrderByDescending(x => x.CreatedAt) });
    }

    [HttpGet("{taskId}")]
    public IActionResult Detail(string taskId)
    {
        var user = CurrentUser();
        var task = FindVisibleTask(user.Id, taskId, Permissions.Read);
        return task is null ? NotFound(new { detail = "Task not found." }) : Ok(new { task });
    }

    [HttpPost("{taskId}/cancel")]
    public async Task<IActionResult> Cancel(string taskId)
    {
        var user = CurrentUser();
        var visible = FindVisibleTask(user.Id, taskId, Permissions.Edit);
        if (visible is null) return NotFound(new { detail = "Task not found." });
        var task = await Store.UpdateTaskAsync(visible.UserId, taskId, "cancelled", 0.0, null);
        return task is null ? NotFound(new { detail = "Task not found." }) : Ok(new { task });
    }

    [HttpPost("{taskId}/retry")]
    public async Task<IActionResult> Retry(string taskId)
    {
        var user = CurrentUser();
        var visible = FindVisibleTask(user.Id, taskId, Permissions.Edit);
        if (visible is null) return NotFound(new { detail = "Task not found." });
        var task = await Store.UpdateTaskAsync(visible.UserId, taskId, "queued", 0.0, null);
        return task is null ? NotFound(new { detail = "Task not found." }) : Ok(new { task });
    }

    TaskItem? FindVisibleTask(int userId, string taskId, Func<ProjectAccess, bool> permission)
    {
        var task = Store.State.Tasks.FirstOrDefault(x => x.Id == taskId);
        if (task is null) return null;
        if (task.UserId == userId) return task;
        return Store.GetProjectAccess(userId, task.ProjectId) is { } access && permission(access) ? task : null;
    }
}
