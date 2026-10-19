using Microsoft.AspNetCore.Mvc;

[Route("api/export")]
[Tags("Export")]
public sealed class ExportController : MaskFlowControllerBase
{
    public ExportController(MaskFlowStore store) : base(store) { }

    [HttpGet]
    public IActionResult List([FromQuery] string? projectId)
    {
        var user = CurrentUser();
        IEnumerable<DatasetExport> source;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            source = Store.State.Exports.Where(x => x.UserId == user.Id);
        }
        else
        {
            var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Export);
            source = Store.State.Exports.Where(x => x.ProjectId == access.Project.Id);
        }
        var exports = source
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                ProjectName = Store.State.Projects.FirstOrDefault(p => p.Id == x.ProjectId)?.Name,
                x.Status,
                x.Size,
                x.CreatedAt,
                x.FinishedAt,
                x.DownloadUrl,
                x.ErrorMessage,
                Split = x.Config.Split,
                Format = x.Config.Format
            });
        return Ok(new { exports });
    }

    // Exports of a shared project are built from (and owned by) the project owner's data.
    [HttpPost("dataset")]
    public async Task<IActionResult> CreateDataset([FromBody] ExportRequest request)
    {
        var user = CurrentUser();
        var split = request.Split ?? new SplitConfig(70, 20, 10);
        DatasetSplitter.Validate(split);
        var ownerId = string.IsNullOrWhiteSpace(request.ProjectId)
            ? user.Id
            : Store.RequireProjectAccess(user.Id, request.ProjectId, Permissions.Export).OwnerId;

        var export = await Store.CreateDatasetExportAsync(ownerId, request with { Split = split });
        return Ok(new { export });
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] ExportRequest request)
    {
        var user = CurrentUser();
        var ownerId = string.IsNullOrWhiteSpace(request.ProjectId)
            ? user.Id
            : Store.RequireProjectAccess(user.Id, request.ProjectId, Permissions.Read).OwnerId;
        if (request.GroupNearDuplicates) await Store.EnsurePerceptualHashesAsync(ownerId, request.ProjectId, HttpContext.RequestAborted);
        var plan = DatasetExportPlan.Create(Store.State, ownerId, request);
        return Ok(new { analysis = DatasetAnalysis.Analyze(plan, request.Split ?? new(70, 20, 10), request.Seed) });
    }

    [HttpGet("{exportId}")]
    public IActionResult Detail(string exportId)
    {
        var user = CurrentUser();
        var export = FindVisibleExport(user.Id, exportId);
        return export is null ? NotFound(new { detail = "Export not found." }) : Ok(new { export });
    }

    [HttpGet("{exportId}/download")]
    public async Task<IActionResult> Download(string exportId)
    {
        var user = CurrentUser();
        var export = FindVisibleExport(user.Id, exportId);
        if (export is null || export.Path is null || !await Store.StoredObjectExistsAsync(export.Path)) return NotFound(new { detail = "Export file not found." });
        return File(await Store.OpenStoredObjectAsync(export.Path), "application/zip", $"{exportId}.zip");
    }

    DatasetExport? FindVisibleExport(int userId, string exportId)
    {
        var export = Store.State.Exports.FirstOrDefault(x => x.Id == exportId);
        if (export is null) return null;
        if (export.UserId == userId) return export;
        return Store.GetProjectAccess(userId, export.ProjectId) is { CanExport: true } ? export : null;
    }
}
