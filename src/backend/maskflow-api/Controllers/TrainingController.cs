using Microsoft.AspNetCore.Mvc;

[Route("api")]
[Tags("Training")]
public sealed class TrainingController(MaskFlowStore store, TrainingWorker worker) : MaskFlowControllerBase(store)
{
    // Base weights an administrator downloaded into the trainer's weights directory.
    [HttpGet("training/weights")]
    public async Task<IActionResult> Weights(CancellationToken cancellationToken)
    {
        CurrentUser();
        try
        {
            return Ok(new { available = true, weights = await worker.ListWeightsAsync(cancellationToken) });
        }
        catch (Exception ex) when (ex is HttpRequestException or SamServiceException or System.Text.Json.JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return Ok(new { available = false, weights = Array.Empty<TrainerWeight>(), error = TrainingWorker.Describe(ex) });
        }
    }

    [HttpGet("projects/{projectId}/trainings")]
    public IActionResult List(string projectId)
    {
        var user = CurrentUser();
        Store.RequireProjectAccess(user.Id, projectId, Permissions.Read);
        return Ok(new
        {
            trainings = Store.State.TrainingRuns.Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).Select(MaskFlowStore.TrainingSummary)
        });
    }

    // Training exports the project's data, so it needs the export permission (owner or reviewer).
    [HttpPost("projects/{projectId}/trainings")]
    public async Task<IActionResult> Create(string projectId, [FromBody] TrainingRequest request, CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Export);
        if (string.IsNullOrWhiteSpace(request.BaseWeight)) return BadRequest(new { detail = "请选择基础权重。" });
        IReadOnlyList<TrainerWeight> weights;
        try
        {
            weights = await worker.ListWeightsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or SamServiceException or System.Text.Json.JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return StatusCode(503, new { detail = TrainingWorker.Describe(ex) });
        }

        var weight = weights.FirstOrDefault(x => x.Name == request.BaseWeight);
        if (weight is null) return BadRequest(new { detail = $"训练服务上没有基础权重 {request.BaseWeight}，请先把它下载到训练权重目录。" });
        var run = await Store.CreateTrainingRunAsync(user.Id, access, request, weight);
        worker.Wake();
        return Ok(new { training = MaskFlowStore.TrainingSummary(run) });
    }

    [HttpGet("trainings/{id}")]
    public IActionResult Get(string id)
    {
        var run = RequireRun(id, Permissions.Read);
        return Ok(new { training = MaskFlowStore.TrainingSummary(run), history = run.History, metrics = run.Metrics });
    }

    [HttpPost("trainings/{id}/cancel")]
    public async Task<IActionResult> Cancel(string id, CancellationToken cancellationToken)
    {
        var run = RequireRun(id, Permissions.Export);
        if (!MaskFlowStore.IsActiveTraining(run)) return Conflict(new { detail = "训练已经结束。" });
        var cancelled = await worker.CancelAsync(id, cancellationToken);
        return Ok(new { training = MaskFlowStore.TrainingSummary(cancelled ?? Store.GetTrainingRun(id)!) });
    }

    [HttpGet("trainings/{id}/artifacts/{name}")]
    public async Task<IActionResult> Artifact(string id, string name)
    {
        var run = RequireRun(id, Permissions.Export);
        var artifact = run.Artifacts.FirstOrDefault(x => x.Name == name);
        if (artifact is null) return NotFound(new { detail = "没有这个训练产物。" });
        var stream = await Store.OpenStoredObjectAsync(artifact.Path);
        return File(stream, MaskFlowStore.ArtifactContentType(name), $"{run.Id}_{name}");
    }

    [HttpDelete("trainings/{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var run = RequireRun(id, Permissions.Manage);
        return await Store.DeleteTrainingRunAsync(run.ProjectId, id) ? Ok(new { ok = true }) : NotFound(new { detail = "Training not found." });
    }

    TrainingRun RequireRun(string id, Func<ProjectAccess, bool> permission)
    {
        var user = CurrentUser();
        var run = Store.GetTrainingRun(id) ?? throw new BadHttpRequestException("Training not found.", 404);
        Store.RequireProjectAccess(user.Id, run.ProjectId, permission);
        return run;
    }
}
