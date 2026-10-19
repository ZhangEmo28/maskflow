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
        var models = Store.State.AiModels
            .Where(x => x.UserId == user.Id && (string.IsNullOrWhiteSpace(projectId) || x.ProjectId is null || x.ProjectId == projectId))
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
        var model = await Store.AddAiModelAsync(user, upload, form["name"].FirstOrDefault(), form["projectId"].FirstOrDefault());
        return Ok(new { model = ToDto(model), user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    [HttpDelete("{modelId}")]
    public async Task<IActionResult> Delete(string modelId)
    {
        var user = CurrentUser();
        if (!await Store.DeleteAiModelAsync(user.Id, modelId)) return NotFound(new { detail = "Model not found." });
        await annotator.DropCachedModelAsync(modelId);
        return Ok(new { ok = true, user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    static object ToDto(AiModel model) => new { model.Id, model.ProjectId, model.Name, model.Size, model.ClassNames, model.CreatedAt };
}
