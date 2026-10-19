using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MaskFlow.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

[Tags("Annotation")]
public sealed class AnnotationController : ControllerBase
{
    private readonly MaskFlowStore store;
    private readonly IHttpClientFactory clientFactory;
    private readonly SamInferenceGate samGate;
    private readonly AutoAnnotationService annotator;
    private readonly BatchAnnotationWorker worker;

    public AnnotationController(MaskFlowStore store, IHttpClientFactory clientFactory, SamInferenceGate samGate, AutoAnnotationService annotator, BatchAnnotationWorker worker)
    {
        this.store = store;
        this.clientFactory = clientFactory;
        this.samGate = samGate;
        this.annotator = annotator;
        this.worker = worker;
    }

    [HttpPost("/api/annotations/auto")]
    public async Task<IActionResult> Auto([FromBody] AnnotationAutoRequest request)
    {
        var user = store.RequireUser(HttpContext);
        store.EnsureAiQuotaAvailable(user);
        var file = store.State.Files.FirstOrDefault(x => x.Id == request.FileId && x.UserId == user.Id);
        if (file is null || !await store.StoredObjectExistsAsync(file.Path)) return NotFound(new { detail = "File not found." });

        var options = AutoAnnotationOptions.Create(request.Mode, request.Conf, request.DefaultLabel, request.ModelId);
        annotator.Validate(user.Id, file, options);
        await using var slot = await samGate.TryAcquireAsync(HttpContext.RequestAborted);
        if (slot is null)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { detail = "Too many concurrent AI requests. Try again shortly." });
        }

        try
        {
            var set = await annotator.RunAsync(user.Id, file, options, recordTask: true, HttpContext.RequestAborted);
            return Ok(new { annotation = set, user = store.PublicUser(store.GetUser(user.Id)!) });
        }
        catch (SamServiceException ex)
        {
            return new ContentResult { Content = ex.Body, ContentType = ex.ContentType ?? "application/json", StatusCode = ex.StatusCode };
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { detail = $"SAM inference service is unavailable: {ex.Message}" });
        }
        catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(504, new { detail = "SAM inference service request timed out." });
        }
    }

    // Queues server-side auto annotation for many images; progress is polled via /api/tasks/{id}.
    [HttpPost("/api/annotations/batch")]
    public async Task<IActionResult> Batch([FromBody] AnnotationBatchRequest request)
    {
        var user = store.RequireUser(HttpContext);
        store.EnsureAiQuotaAvailable(user);
        var project = store.State.Projects.FirstOrDefault(x => x.Id == request.ProjectId && x.UserId == user.Id);
        if (project is null) return NotFound(new { detail = "Project not found." });

        var options = AutoAnnotationOptions.Create(request.Mode, request.Conf, request.DefaultLabel, request.ModelId);
        var files = store.State.Files
            .Where(x => x.UserId == user.Id && x.ProjectId == project.Id && x.Kind == "image")
            .OrderBy(x => x.Id)
            .ToList();
        if (files.Count == 0) return BadRequest(new { detail = "项目中还没有图片。" });
        annotator.Validate(user.Id, files[0], options);

        var annotated = store.State.AnnotationSets.Where(x => x.UserId == user.Id).Select(x => x.FileId).ToHashSet();
        var fileIds = (request.Scope == "all" ? files : files.Where(x => !annotated.Contains(x.Id)))
            .Select(x => x.Id)
            .ToList();
        if (fileIds.Count == 0) return BadRequest(new { detail = "没有需要处理的图片：所有图片都已有标注。" });

        var active = store.State.Tasks.Any(x => x.UserId == user.Id && x.ProjectId == project.Id
            && x.Type == MaskFlowStore.BatchAnnotationTaskType && x.Status is "queued" or "running");
        if (active) return Conflict(new { detail = "该项目已有正在进行的批量标注任务。" });

        var task = await store.CreateBatchAnnotationTaskAsync(user.Id, request with { Mode = options.Mode }, fileIds);
        worker.Wake();
        return Ok(new { task });
    }

    [HttpPost("/api/annotations/points")]
    public async Task<IActionResult> Points([FromBody] AnnotationPointsRequest request)
    {
        var user = store.RequireUser(HttpContext);
        try
        {
            store.EnsureAiQuotaAvailable(user);
        }
        catch (BadHttpRequestException ex)
        {
            return StatusCode(ex.StatusCode, new { detail = ex.Message });
        }

        if (request.Points is null || request.Points.Count == 0)
        {
            return BadRequest(new { detail = "At least one prompt point is required." });
        }

        if (request.Labels is null || request.Labels.Count != request.Points.Count)
        {
            return BadRequest(new { detail = "points and labels length must match." });
        }

        if (!request.Labels.Contains(1))
        {
            return BadRequest(new { detail = "At least one positive point (label=1) is required." });
        }

        var file = store.State.Files.FirstOrDefault(x => x.Id == request.FileId && x.UserId == user.Id);
        if (file is null || !await store.StoredObjectExistsAsync(file.Path)) return NotFound(new { detail = "File not found." });

        await using var slot = await samGate.TryAcquireAsync(HttpContext.RequestAborted);
        if (slot is null)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { detail = "Too many concurrent AI requests. Try again shortly." });
        }

        using var content = new MultipartFormDataContent();
        await using var stream = await store.OpenStoredObjectAsync(file.Path);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? "application/octet-stream");
        content.Add(fileContent, "image", file.Name);
        content.Add(new StringContent(JsonSerializer.Serialize(request.Points)), "points");
        content.Add(new StringContent(JsonSerializer.Serialize(request.Labels)), "labels");
        content.Add(new StringContent(request.Conf.ToString(CultureInfo.InvariantCulture)), "conf");

        try
        {
            var client = clientFactory.CreateClient("sam");
            var response = await client.PostAsync("/api/segment/points", content, HttpContext.RequestAborted);
            var body = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
            if (response.IsSuccessStatusCode)
            {
                await store.ConsumeAiQuotaAsync(user.Id, 1);
            }

            return new ContentResult
            {
                Content = body,
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
                StatusCode = (int)response.StatusCode
            };
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { detail = $"SAM inference service is unavailable: {ex.Message}" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { detail = "SAM inference service request timed out." });
        }
    }

    [HttpGet("/api/annotations/file/{fileId:int}")]
    public IActionResult GetByFile(int fileId)
    {
        var user = store.RequireUser(HttpContext);
        var set = store.GetAnnotationSet(user.Id, fileId);
        return set is null ? NotFound(new { detail = "Annotation not found." }) : Ok(new { annotation = set });
    }

    [HttpPut("/api/annotations/file/{fileId:int}")]
    public async Task<IActionResult> SaveByFile(int fileId, [FromBody] AnnotationSaveRequest request)
    {
        var user = store.RequireUser(HttpContext);
        var set = await store.SaveAnnotationSetAsync(user.Id, request with { FileId = fileId });
        return Ok(new { annotation = set });
    }

    [HttpDelete("/api/annotations/file/{fileId:int}/items/{annotationId}")]
    public async Task<IActionResult> DeleteItem(int fileId, string annotationId)
    {
        var user = store.RequireUser(HttpContext);
        var set = store.GetAnnotationSet(user.Id, fileId);
        if (set is null) return NotFound(new { detail = "Annotation not found." });
        var next = await store.SaveAnnotationSetAsync(user.Id, new AnnotationSaveRequest(fileId, set.Width, set.Height, set.Annotations.Where(x => x.Id != annotationId).ToList()));
        return Ok(new { annotation = next });
    }

    [HttpPost("/api/annotation/masks")]
    public async Task<IActionResult> Masks()
    {
        var user = store.RequireUser(HttpContext);
        try
        {
            store.EnsureAiQuotaAvailable(user);
        }
        catch (BadHttpRequestException ex)
        {
            return StatusCode(ex.StatusCode, new { detail = ex.Message });
        }

        if (!Request.HasFormContentType) return BadRequest(new { detail = "Multipart form data is required." });
        var form = await Request.ReadFormAsync();
        if (form.Files.Count == 0) return BadRequest(new { detail = "Image file is required." });

        foreach (var file in form.Files)
        {
            try
            {
                await UploadValidator.ValidateImageAsync(file, HttpContext.RequestAborted);
            }
            catch (BadHttpRequestException ex)
            {
                return StatusCode(ex.StatusCode, new { detail = ex.Message });
            }
        }

        await using var slot = await samGate.TryAcquireAsync(HttpContext.RequestAborted);
        if (slot is null)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { detail = "Too many concurrent AI requests. Try again shortly." });
        }

        using var content = new MultipartFormDataContent();
        foreach (var field in form)
        {
            foreach (var value in field.Value)
            {
                content.Add(new StringContent(value ?? ""), field.Key);
            }
        }

        foreach (var file in form.Files)
        {
            var stream = file.OpenReadStream();
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? "application/octet-stream");
            content.Add(fileContent, file.Name, file.FileName);
        }

        try
        {
            var client = clientFactory.CreateClient("sam");
            var response = await client.PostAsync("/api/annotation/masks", content, HttpContext.RequestAborted);
            var body = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
            if (response.IsSuccessStatusCode) await store.ConsumeAiQuotaAsync(user.Id, 1);
            return new ContentResult
            {
                Content = body,
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
                StatusCode = (int)response.StatusCode
            };
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { detail = $"SAM inference service is unavailable: {ex.Message}" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { detail = "SAM inference service request timed out." });
        }
    }
}
