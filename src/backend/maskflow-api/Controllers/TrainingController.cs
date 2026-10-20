using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// Short-lived links for big downloads (training package, weights): the browser saves them with its
// own download manager instead of buffering gigabytes in JavaScript. A link is bound to the user,
// the run and the file, and expires after five minutes (or when the API restarts).
public static class TrainingDownloadLinks
{
    static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static string Create(int userId, string runId, string target)
    {
        var expires = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        return $"{userId}.{expires}.{Sign(userId, runId, target, expires)}";
    }

    public static int? Validate(string? token, string runId, string target)
    {
        var parts = (token ?? "").Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var userId) || !long.TryParse(parts[1], out var expires)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires) return null;
        var expected = Encoding.ASCII.GetBytes(Sign(userId, runId, target, expires));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[2])) ? userId : null;
    }

    static string Sign(int userId, string runId, string target, long expires) =>
        Convert.ToHexString(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes($"{userId}|{runId}|{target}|{expires}"))).ToLowerInvariant();
}

[Route("api")]
[Tags("Training")]
public sealed class TrainingController(MaskFlowStore store, TrainingWorker worker) : MaskFlowControllerBase(store)
{
    static bool IsTrainerError(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or SamServiceException or System.Text.Json.JsonException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    // Installed base weights plus the official catalog (with download progress) from the trainer.
    [HttpGet("training/weights")]
    public async Task<IActionResult> Weights(CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        var canManage = Store.CanManageBaseWeights(user);
        try
        {
            var overview = await worker.GetWeightsOverviewAsync(cancellationToken);
            return Ok(new
            {
                available = true,
                weights = overview.GetProperty("weights"),
                catalog = overview.TryGetProperty("catalog", out var catalog) ? catalog : default,
                writable = overview.TryGetProperty("writable", out var writable) && writable.GetBoolean(),
                inUse = overview.TryGetProperty("inUse", out var inUse) ? inUse.GetString() : null,
                canManage
            });
        }
        catch (Exception ex) when (IsTrainerError(ex, cancellationToken))
        {
            return Ok(new { available = false, weights = Array.Empty<TrainerWeight>(), catalog = Array.Empty<object>(), canManage, error = TrainingWorker.Describe(ex) });
        }
    }

    [HttpPost("training/weights/{name}/download")]
    public async Task<IActionResult> DownloadWeight(string name, CancellationToken cancellationToken)
    {
        RequireWeightAdmin();
        try
        {
            await worker.DownloadWeightAsync(name, cancellationToken);
            return Ok(new { ok = true });
        }
        catch (SamServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { detail = ex.Detail });
        }
        catch (Exception ex) when (IsTrainerError(ex, cancellationToken))
        {
            return StatusCode(503, new { detail = TrainingWorker.Describe(ex) });
        }
    }

    [HttpDelete("training/weights/{name}")]
    public async Task<IActionResult> DeleteWeight(string name, CancellationToken cancellationToken)
    {
        RequireWeightAdmin();
        if (Store.State.TrainingRuns.Any(x => MaskFlowStore.IsActiveTraining(x) && x.Request.BaseWeight == name))
        {
            return Conflict(new { detail = "有训练正在排队或使用这个权重，结束后再删除。" });
        }

        try
        {
            await worker.DeleteWeightAsync(name, cancellationToken);
            return Ok(new { ok = true });
        }
        catch (SamServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { detail = ex.Detail });
        }
        catch (Exception ex) when (IsTrainerError(ex, cancellationToken))
        {
            return StatusCode(503, new { detail = TrainingWorker.Describe(ex) });
        }
    }

    void RequireWeightAdmin()
    {
        if (!Store.CanManageBaseWeights(CurrentUser()))
        {
            throw new BadHttpRequestException("只有管理员可以下载或删除基础权重（MASKFLOW_ADMIN_EMAILS，未设置时为第一个注册的账号）。", 403);
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
        catch (Exception ex) when (IsTrainerError(ex, cancellationToken))
        {
            return StatusCode(503, new { detail = TrainingWorker.Describe(ex) });
        }

        var weight = weights.FirstOrDefault(x => x.Name == request.BaseWeight);
        if (weight is null) return BadRequest(new { detail = $"训练服务上没有基础权重 {request.BaseWeight}，请先把它下载到训练权重目录。" });
        var run = await Store.CreateTrainingRunAsync(user.Id, access, request, weight);
        worker.Wake();
        return Ok(new { training = MaskFlowStore.TrainingSummary(run) });
    }

    // Readiness of the project's data for a training run (minimums, per-class split, what to add).
    [HttpPost("projects/{projectId}/trainings/check")]
    public async Task<IActionResult> Check(string projectId, [FromBody] TrainingRequest request)
    {
        var user = CurrentUser();
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Read);
        var task = string.IsNullOrWhiteSpace(request.BaseWeight)
            ? MaskFlowStore.NormalizeDataType(access.Project.DataType) == "segmentation" ? "segment" : "detect"
            : TrainingReadiness.TaskFromWeightName(request.BaseWeight);
        return Ok(new { readiness = await Store.CheckTrainingDataAsync(access, request, task) });
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

    // A download link for the package ("bundle") or one artifact path, usable without the auth header.
    [HttpPost("trainings/{id}/download-link")]
    public IActionResult DownloadLink(string id, [FromQuery] string? path)
    {
        var user = CurrentUser();
        var run = RequireRun(id, Permissions.Export);
        if (path is not null && MaskFlowStore.FindArtifact(run, path) is null) return NotFound(new { detail = "没有这个训练产物。" });
        var token = Uri.EscapeDataString(TrainingDownloadLinks.Create(user.Id, run.Id, path ?? "bundle"));
        var url = path is null
            ? $"/api/trainings/{run.Id}/bundle?token={token}"
            : $"/api/trainings/{run.Id}/artifacts/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}?token={token}";
        return Ok(new { url });
    }

    // Result images (plots, prediction samples) are visible to every member; weights, the dataset
    // and other files need the export permission. Also reachable through a download link.
    [AllowAnonymous]
    [HttpGet("trainings/{id}/artifacts/{*name}")]
    public async Task<IActionResult> Artifact(string id, string name, [FromQuery] string? token)
    {
        var isImage = Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
        var run = RequireRun(id, isImage ? Permissions.Read : Permissions.Export, token, name);
        var artifact = MaskFlowStore.FindArtifact(run, name);
        if (artifact is null) return NotFound(new { detail = "没有这个训练产物。" });
        var stream = await Store.OpenStoredObjectAsync(artifact.Path);
        return File(stream, MaskFlowStore.ArtifactContentType(name), $"{run.Id}_{Path.GetFileName(name)}");
    }

    // One zip with everything needed to reproduce and use the run: base weight, dataset, training
    // script, trained weights and every file the training produced.
    [AllowAnonymous]
    [HttpGet("trainings/{id}/bundle")]
    public async Task<IActionResult> Bundle(string id, [FromQuery] string? token, CancellationToken cancellationToken)
    {
        var run = RequireRun(id, Permissions.Export, token, "bundle");
        if (MaskFlowStore.IsActiveTraining(run)) return Conflict(new { detail = "训练还没结束，结束后再下载训练包。" });
        if (run.Artifacts.Count == 0) return NotFound(new { detail = "这次训练没有保存任何文件。" });
        var path = await Store.BuildTrainingBundleAsync(run, cancellationToken);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var name = $"{MaskFlowStore.SanitizeFileName(run.Name)}_{run.Id}.zip";
        return File(stream, "application/zip", name);
    }

    [HttpDelete("trainings/{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var run = RequireRun(id, Permissions.Manage);
        return await Store.DeleteTrainingRunAsync(run.ProjectId, id) ? Ok(new { ok = true }) : NotFound(new { detail = "Training not found." });
    }

    // With a download link the user comes from the token (still re-checked against the project).
    TrainingRun RequireRun(string id, Func<ProjectAccess, bool> permission, string? token = null, string? target = null)
    {
        var userId = token is not null && target is not null
            ? TrainingDownloadLinks.Validate(token, id, target) ?? throw new BadHttpRequestException("下载链接无效或已过期，请重新点击下载。", 401)
            : CurrentUser().Id;
        var run = Store.GetTrainingRun(id) ?? throw new BadHttpRequestException("Training not found.", 404);
        Store.RequireProjectAccess(userId, run.ProjectId, permission);
        return run;
    }
}
