using Microsoft.AspNetCore.Mvc;

[Route("api/projects")]
[Tags("Projects")]
public sealed class ProjectsController : MaskFlowControllerBase
{
    readonly ProjectService projectService;
    readonly AccountMailer mailer;
    readonly VideoTaskWorker videoWorker;

    public ProjectsController(MaskFlowStore store, ProjectService projectService, AccountMailer mailer, VideoTaskWorker videoWorker) : base(store)
    {
        this.projectService = projectService;
        this.mailer = mailer;
        this.videoWorker = videoWorker;
    }

    // ---- Videos: upload a clip, frames are extracted in the background into project images ----

    [HttpPost("{projectId}/videos")]
    public async Task<IActionResult> UploadVideo(string projectId)
    {
        var user = CurrentUser();
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Edit);
        var form = await Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        if (file is null) return BadRequest(new { detail = "请选择视频文件。" });
        var everySeconds = double.TryParse(form["everySeconds"].FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 1;
        var maxFrames = int.TryParse(form["maxFrames"].FirstOrDefault(), out var frames) ? frames : 300;
        var (video, task) = await Store.AddVideoAsync(Store.GetUser(access.OwnerId)!, file, projectId, everySeconds, maxFrames);
        videoWorker.Wake();
        return Ok(new { video = VideoDto(video), task, user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    [HttpGet("{projectId}/videos")]
    public IActionResult Videos(string projectId)
    {
        var user = CurrentUser();
        Store.RequireProjectAccess(user.Id, projectId, Permissions.Read);
        return Ok(new { videos = Store.State.Videos.Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).Select(VideoDto) });
    }

    [HttpDelete("{projectId}/videos/{videoId}")]
    public async Task<IActionResult> DeleteVideo(string projectId, string videoId)
    {
        var user = CurrentUser();
        Store.RequireProjectAccess(user.Id, projectId, Permissions.Review);
        return await Store.DeleteVideoAsync(projectId, videoId) ? Ok(new { ok = true }) : NotFound(new { detail = "Video not found." });
    }

    static object VideoDto(VideoItem video) => new
    {
        video.Id,
        video.Name,
        video.Size,
        video.Fps,
        video.DurationSec,
        video.EverySeconds,
        FrameCount = video.FrameFileIds.Count,
        FirstFrameId = video.FrameFileIds.FirstOrDefault(),
        video.Status,
        video.Error,
        video.CreatedAt
    };

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
        var labels = projectService.GetLabels(user.Id, projectId);
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Read);
        return Ok(new { labels, prompts = Store.GetLabelPrompts(access.OwnerId, projectId) });
    }

    // Import an existing dataset: a zip (images + YOLO/COCO/LabelMe/VOC annotations) or a single
    // COCO/LabelMe/VOC file whose images were uploaded before. Annotations match images by file name.
    [HttpPost("{projectId}/import")]
    public async Task<IActionResult> Import(string projectId)
    {
        var user = CurrentUser();
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Review);
        var form = await Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        if (file is null) return BadRequest(new { detail = "请选择要导入的 zip 或标注文件。" });
        var options = new ImportOptions(
            form["format"].FirstOrDefault(),
            !string.Equals(form["confirmed"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase),
            form["onExisting"].FirstOrDefault() ?? "skip",
            !string.Equals(form["importImages"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase));
        await using var stream = file.OpenReadStream();
        var summary = await Store.ImportDatasetAsync(Store.GetUser(access.OwnerId)!, projectId, stream, file.FileName, options);
        return Ok(new { summary, user = Store.PublicUser(Store.GetUser(user.Id)!) });
    }

    // English text prompts used by SAM 3 when auto-annotating with project labels.
    [HttpPut("{projectId}/label-prompts")]
    public async Task<IActionResult> SaveLabelPrompts(string projectId, [FromBody] LabelPromptsRequest request)
    {
        var user = CurrentUser();
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Review);
        return Ok(new { prompts = await Store.SaveLabelPromptsAsync(access.OwnerId, projectId, request.Prompts) });
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
        var labels = await projectService.DeleteLabelAsync(user.Id, projectId, labelName, replaceWith);
        return Ok(new { labels });
    }

    // ---- Team ----

    [HttpGet("{projectId}/members")]
    public IActionResult Members(string projectId)
    {
        var user = CurrentUser();
        return Ok(Store.ListMembers(Store.RequireProjectAccess(user.Id, projectId, Permissions.Read)));
    }

    [HttpPost("{projectId}/members")]
    public async Task<IActionResult> Invite(string projectId, [FromBody] MemberInviteRequest request)
    {
        var user = CurrentUser();
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Manage);
        var (member, invite, token, existing) = await Store.InviteMemberAsync(user.Id, access, request.Email, request.Role);
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        await mailer.SendProjectInviteAsync(email, user.Username ?? user.Email, access.Project.Name, member?.Role ?? invite!.Role, existing);
        return Ok(new
        {
            member,
            invite = invite is null ? null : new { invite.Id, invite.Email, invite.Role, invite.ExpiresAt },
            // Owners can share the link themselves when mail is not configured.
            inviteUrl = token is null ? null : $"/auth.html?invite={token}",
            emailed = mailer.CanDeliver
        });
    }

    [HttpPut("{projectId}/members/{memberId:int}")]
    public async Task<IActionResult> UpdateRole(string projectId, int memberId, [FromBody] MemberRoleRequest request)
    {
        var user = CurrentUser();
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Manage);
        return await Store.UpdateMemberRoleAsync(access, memberId, request.Role) ? Ok(new { ok = true }) : NotFound(new { detail = "Member not found." });
    }

    // Owners remove anyone; members may remove themselves (leave the project).
    [HttpDelete("{projectId}/members/{memberId:int}")]
    public async Task<IActionResult> RemoveMember(string projectId, int memberId)
    {
        var user = CurrentUser();
        Store.RequireProjectAccess(user.Id, projectId, memberId == user.Id ? Permissions.Read : Permissions.Manage);
        return await Store.RemoveMemberAsync(projectId, memberId) ? Ok(new { ok = true }) : NotFound(new { detail = "Member not found." });
    }

    [HttpDelete("{projectId}/invites/{inviteId}")]
    public async Task<IActionResult> RevokeInvite(string projectId, string inviteId)
    {
        var user = CurrentUser();
        Store.RequireProjectAccess(user.Id, projectId, Permissions.Manage);
        return await Store.RevokeInviteAsync(projectId, inviteId) ? Ok(new { ok = true }) : NotFound(new { detail = "Invite not found." });
    }

    [HttpPost("{projectId}/assignments")]
    public async Task<IActionResult> Assign(string projectId, [FromBody] AssignmentRequest request)
    {
        var user = CurrentUser();
        var access = Store.RequireProjectAccess(user.Id, projectId, Permissions.Review);
        return Ok(new { changed = await Store.AssignFilesAsync(user.Id, access, request) });
    }
}

[Route("api/invites")]
[Tags("Projects")]
public sealed class InvitesController : MaskFlowControllerBase
{
    public InvitesController(MaskFlowStore store) : base(store) { }

    [HttpPost("{token}/accept")]
    public async Task<IActionResult> Accept(string token)
    {
        var user = CurrentUser();
        var member = await Store.AcceptInviteAsync(user, token);
        return Ok(new { member, projectId = member?.ProjectId });
    }
}
