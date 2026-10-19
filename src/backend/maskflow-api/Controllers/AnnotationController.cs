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

    public AnnotationController(MaskFlowStore store, IHttpClientFactory clientFactory, SamInferenceGate samGate)
    {
        this.store = store;
        this.clientFactory = clientFactory;
        this.samGate = samGate;
    }

    [HttpPost("/api/annotations/auto")]
    public async Task<IActionResult> Auto([FromBody] AnnotationAutoRequest request)
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

        var file = store.State.Files.FirstOrDefault(x => x.Id == request.FileId && x.UserId == user.Id);
        if (file is null || !await store.StoredObjectExistsAsync(file.Path)) return NotFound(new { detail = "File not found." });
        if (string.IsNullOrWhiteSpace(file.ProjectId))
        {
            return BadRequest(new { detail = "File must belong to a project before auto annotation." });
        }

        var projectLabels = store.GetProjectLabels(user.Id, file.ProjectId);
        if (projectLabels.Count == 0)
        {
            return BadRequest(new { detail = "Add at least one label to the project before running auto annotation." });
        }

        if (!string.IsNullOrWhiteSpace(request.DefaultLabel)
            && MaskFlowStore.ResolveLabelClassId(request.DefaultLabel, projectLabels) < 0)
        {
            return BadRequest(new { detail = "DefaultLabel is not in project labels." });
        }

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
        content.Add(new StringContent(request.Conf.ToString(CultureInfo.InvariantCulture)), "conf");

        try
        {
            var client = clientFactory.CreateClient("sam");
            var response = await client.PostAsync("/api/annotation/masks", content, HttpContext.RequestAborted);
            var body = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                return new ContentResult
                {
                    Content = body,
                    ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
                    StatusCode = (int)response.StatusCode
                };
            }

            var saveRequest = AutoAnnotationParser.Parse(file.Id, body, request.DefaultLabel, projectLabels);
            var (set, _) = await store.PersistAutoAnnotationAsync(user.Id, saveRequest, file);
            return Ok(new { annotation = set, user = store.PublicUser(store.GetUser(user.Id)!) });
        }
        catch (BadHttpRequestException ex)
        {
            return StatusCode(ex.StatusCode, new { detail = ex.Message });
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
