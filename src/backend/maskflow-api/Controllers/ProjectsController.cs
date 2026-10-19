using Microsoft.AspNetCore.Mvc;

[Route("api/projects")]
[Tags("Projects")]
public sealed class ProjectsController : MaskFlowControllerBase
{
    readonly ProjectService projectService;

    public ProjectsController(MaskFlowStore store, ProjectService projectService) : base(store)
    {
        this.projectService = projectService;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProjectCreate request)
    {
        var user = CurrentUser();
        var project = await projectService.CreateAsync(user.Id, request);
        return Ok(new { project });
    }

    [HttpGet]
    public IActionResult List()
    {
        var user = CurrentUser();
        return Ok(new { projects = projectService.List(user.Id) });
    }

    [HttpGet("{projectId}")]
    public IActionResult Detail(string projectId)
    {
        var user = CurrentUser();
        var project = projectService.Detail(user.Id, projectId);
        return project is null ? NotFound(new { detail = "Project not found." }) : Ok(new { project });
    }

    [HttpPut("{projectId}")]
    public async Task<IActionResult> Update(string projectId, [FromBody] ProjectCreate request)
    {
        var user = CurrentUser();
        var project = await projectService.UpdateAsync(user.Id, projectId, request);
        return project is null ? NotFound(new { detail = "Project not found." }) : Ok(new { project });
    }

    [HttpPost("{projectId}/copy")]
    public async Task<IActionResult> Copy(string projectId, [FromBody] ProjectCopyRequest request)
    {
        var user = CurrentUser();
        var project = await projectService.CopyAsync(user.Id, projectId, request);
        return project is null ? NotFound(new { detail = "Project not found." }) : Ok(new { project, user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    [HttpDelete("{projectId}")]
    public async Task<IActionResult> Delete(string projectId)
    {
        var user = CurrentUser();
        var deleted = await projectService.DeleteAsync(user.Id, projectId);
        return deleted ? Ok(new { ok = true, user = Store.PublicUser(Store.GetUser(user.Id)!) }) : NotFound(new { detail = "Project not found." });
    }

    [HttpGet("{projectId}/labels")]
    public IActionResult Labels(string projectId)
    {
        var user = CurrentUser();
        return Ok(new { labels = projectService.GetLabels(user.Id, projectId), prompts = Store.GetLabelPrompts(user.Id, projectId) });
    }

    // Import an existing dataset: a zip (images + YOLO/COCO/LabelMe/VOC annotations) or a single
    // COCO/LabelMe/VOC file whose images were uploaded before. Annotations match images by file name.
    [HttpPost("{projectId}/import")]
    public async Task<IActionResult> Import(string projectId)
    {
        var user = CurrentUser();
        var form = await Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        if (file is null) return BadRequest(new { detail = "请选择要导入的 zip 或标注文件。" });
        var options = new ImportOptions(
            form["format"].FirstOrDefault(),
            !string.Equals(form["confirmed"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase),
            form["onExisting"].FirstOrDefault() ?? "skip",
            !string.Equals(form["importImages"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase));
        await using var stream = file.OpenReadStream();
        var summary = await Store.ImportDatasetAsync(user, projectId, stream, file.FileName, options);
        return Ok(new { summary, user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    // English text prompts used by SAM 3 when auto-annotating with project labels.
    [HttpPut("{projectId}/label-prompts")]
    public async Task<IActionResult> SaveLabelPrompts(string projectId, [FromBody] LabelPromptsRequest request)
    {
        var user = CurrentUser();
        return Ok(new { prompts = await Store.SaveLabelPromptsAsync(user.Id, projectId, request.Prompts) });
    }

    [HttpPut("{projectId}/labels")]
    public async Task<IActionResult> SaveLabels(string projectId, [FromBody] ProjectLabelsRequest request)
    {
        var user = CurrentUser();
        var labels = await projectService.SaveLabelsAsync(user.Id, projectId, request.Labels);
        return Ok(new { labels });
    }

    [HttpDelete("{projectId}/labels/{labelName}")]
    public async Task<IActionResult> DeleteLabel(string projectId, string labelName, [FromQuery] string? replaceWith = null)
    {
        var user = CurrentUser();
        try
        {
            var labels = await projectService.DeleteLabelAsync(user.Id, projectId, labelName, replaceWith);
            return Ok(new { labels });
        }
        catch (BadHttpRequestException ex)
        {
            return StatusCode(ex.StatusCode, new { detail = ex.Message });
        }
    }
}
