using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

[Route("api/files")]
[Tags("Files")]
public sealed class FilesController : MaskFlowControllerBase
{
    public FilesController(MaskFlowStore store) : base(store) { }

    // Uploads into a shared project are stored under (and counted against) the project owner.
    [HttpPost("upload")]
    public async Task<IActionResult> Upload()
    {
        var user = CurrentUser();
        var form = await Request.ReadFormAsync();
        var projectId = form["projectId"].FirstOrDefault();
        var owner = user;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            owner = Store.GetUser(Store.RequireProjectAccess(user.Id, projectId, Permissions.Edit).OwnerId)!;
        }

        var files = new List<FileItem>();
        foreach (var upload in form.Files)
        {
            files.Add(await Store.SaveUploadAsync(owner, upload, projectId));
        }

        return Ok(new { files, user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    [HttpGet]
    public IActionResult List([FromQuery] string? projectId)
    {
        var user = CurrentUser();
        var ownerId = user.Id;
        IEnumerable<FileItem> source;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            ownerId = Store.RequireProjectAccess(user.Id, projectId, Permissions.Read).OwnerId;
            source = Store.State.Files.Where(x => x.ProjectId == projectId);
        }
        else
        {
            source = Store.State.Files.Where(x => x.UserId == user.Id);
        }

        var assignments = Store.State.FileAssignments.ToDictionary(x => x.FileId, x => x.UserId);
        var frames = Store.State.Videos
            .SelectMany(video => video.FrameFileIds.Select((id, index) => (id, video.Id, Index: video.FrameIndexes.ElementAtOrDefault(index))))
            .GroupBy(x => x.id)
            .ToDictionary(x => x.Key, x => x.First());
        var files = source
            .OrderByDescending(x => x.CreatedAt)
            .Select(file =>
            {
                var set = Store.GetAnnotationSet(ownerId, file.Id);
                var unconfirmed = set?.Annotations.Where(x => !x.Confirmed).ToList() ?? [];
                var assignedTo = assignments.TryGetValue(file.Id, out var assignee) ? assignee : (int?)null;
                return new
                {
                    file.Id,
                    file.UserId,
                    file.ProjectId,
                    file.Name,
                    file.Size,
                    file.Kind,
                    file.ContentType,
                    file.CreatedAt,
                    file.DownloadUrl,
                    Annotated = set is not null,
                    AnnotationCount = set?.Annotations.Count ?? 0,
                    // Review queue ordering: images whose least certain unreviewed target is lowest come first.
                    UnconfirmedCount = unconfirmed.Count,
                    MinConfidence = unconfirmed.Count == 0 ? (double?)null : unconfirmed.Min(x => x.Confidence),
                    ReviewStatus = set?.ReviewStatus,
                    ReviewComment = set?.ReviewComment,
                    AssignedTo = assignedTo,
                    AssignedToEmail = assignedTo is null ? null : Store.GetUser(assignedTo.Value)?.Email,
                    VideoId = frames.TryGetValue(file.Id, out var frame) ? frame.Id : null,
                    FrameIndex = frames.TryGetValue(file.Id, out var frameAt) ? frameAt.Index : (int?)null
                };
            });
        return Ok(new { files, user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    [HttpGet("{fileId:int}/download")]
    public async Task<IActionResult> Download(int fileId)
    {
        var user = CurrentUser();
        var (file, _, _) = Store.RequireFileAccess(user.Id, fileId, Permissions.Read);
        if (!await Store.StoredObjectExistsAsync(file.Path)) return NotFound(new { detail = "File not found." });
        var provider = new FileExtensionContentTypeProvider();
        provider.TryGetContentType(file.Name, out var contentType);
        return File(await Store.OpenStoredObjectAsync(file.Path), contentType ?? "application/octet-stream", file.Name);
    }

    [HttpDelete("{fileId:int}")]
    public async Task<IActionResult> Delete(int fileId)
    {
        var user = CurrentUser();
        var (_, ownerId, _) = Store.RequireFileAccess(user.Id, fileId, Permissions.Review);
        var deleted = await Store.DeleteFileAsync(ownerId, fileId);
        return deleted ? Ok(new { ok = true, user = Store.PublicUser(Store.GetUser(user.Id)!) }) : NotFound(new { detail = "File not found." });
    }
}
