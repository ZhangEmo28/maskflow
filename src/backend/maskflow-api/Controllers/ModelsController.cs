using Microsoft.AspNetCore.Mvc;

// User-uploaded YOLO models (ONNX only) used for pre-annotation.
[Route("api/models")]
[Tags("Models")]
public sealed class ModelsController : MaskFlowControllerBase
{
    readonly AutoAnnotationService annotator;

    public ModelsController(MaskFlowStore store, AutoAnnotationService annotator) : base(store)
    {
        this.annotator = annotator;
    }

    [HttpGet]
    public IActionResult List([FromQuery] string? projectId)
    {
        var user = CurrentUser();
        // In a shared project, members see the models uploaded to that project (stored under the owner).
        var ownerId = string.IsNullOrWhiteSpace(projectId) ? user.Id : Store.RequireProjectAccess(user.Id, projectId, Permissions.Read).OwnerId;
        var models = Store.State.AiModels
            .Where(x => x.UserId == ownerId && (string.IsNullOrWhiteSpace(projectId) || x.ProjectId == projectId || (x.ProjectId is null && ownerId == user.Id)))
            .OrderByDescending(x => x.CreatedAt)
            .Select(ToDto);
        return Ok(new { models });
    }

    [HttpPost]
    public async Task<IActionResult> Upload()
    {
        var user = CurrentUser();
        var form = await Request.ReadFormAsync();
        var upload = form.Files.FirstOrDefault();
        if (upload is null) return BadRequest(new { detail = "请选择 .onnx 模型文件。" });
        var projectId = form["projectId"].FirstOrDefault();
        var owner = string.IsNullOrWhiteSpace(projectId)
            ? user
            : Store.GetUser(Store.RequireProjectAccess(user.Id, projectId, Permissions.Review).OwnerId)!;
        var model = await Store.AddAiModelAsync(owner, upload, form["name"].FirstOrDefault(), projectId);
        return Ok(new { model = ToDto(model), user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    [HttpDelete("{modelId}")]
    public async Task<IActionResult> Delete(string modelId)
    {
        var user = CurrentUser();
        var model = Store.State.AiModels.FirstOrDefault(x => x.Id == modelId);
        var allowed = model is not null && (model.UserId == user.Id || Store.GetProjectAccess(user.Id, model.ProjectId) is { CanReview: true });
        if (!allowed || !await Store.DeleteAiModelAsync(model!.UserId, modelId)) return NotFound(new { detail = "Model not found." });
        await annotator.DropCachedModelAsync(modelId);
        return Ok(new { ok = true, user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    static object ToDto(AiModel model) => new { model.Id, model.ProjectId, model.Name, model.Size, model.ClassNames, model.CreatedAt };
}
