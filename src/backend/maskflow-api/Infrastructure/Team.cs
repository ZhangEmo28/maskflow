using System.Security.Cryptography;

// owner: everything; reviewer: edit, review, labels, export; annotator: edit + submit; viewer: read-only.
public sealed record ProjectAccess(Project Project, int OwnerId, string Role)
{
    public static readonly string[] MemberRoles = ["reviewer", "annotator", "viewer"];

    public bool CanEdit => Role is "owner" or "reviewer" or "annotator";
    public bool CanReview => Role is "owner" or "reviewer";
    public bool CanManage => Role == "owner";
    public bool CanExport => Role is "owner" or "reviewer";
}

public static class Permissions
{
    public static readonly Func<ProjectAccess, bool> Read = _ => true;
    public static readonly Func<ProjectAccess, bool> Edit = x => x.CanEdit;
    public static readonly Func<ProjectAccess, bool> Review = x => x.CanReview;
    public static readonly Func<ProjectAccess, bool> Manage = x => x.CanManage;
    public static readonly Func<ProjectAccess, bool> Export = x => x.CanExport;
}

public sealed partial class MaskFlowStore
{
    // ---- Access resolution: every project/file scoped endpoint goes through these ----

    public ProjectAccess? GetProjectAccess(int userId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        var project = State.Projects.FirstOrDefault(x => x.Id == projectId);
        if (project is null) return null;
        if (project.UserId == userId) return new ProjectAccess(project, userId, "owner");
        var member = State.ProjectMembers.FirstOrDefault(x => x.ProjectId == projectId && x.UserId == userId);
        return member is null ? null : new ProjectAccess(project, project.UserId, member.Role);
    }

    // Non-members get 404 (no hint that the project exists); members without the permission get 403.
    public ProjectAccess RequireProjectAccess(int userId, string? projectId, Func<ProjectAccess, bool> permission)
    {
        var access = GetProjectAccess(userId, projectId) ?? throw new BadHttpRequestException("Project not found.", 404);
        if (!permission(access)) throw new BadHttpRequestException("当前角色没有这个权限。", 403);
        return access;
    }

    // Files outside a project are personal and only visible to their owner.
    public (FileItem File, int OwnerId, ProjectAccess? Access) RequireFileAccess(int userId, int fileId, Func<ProjectAccess, bool> permission)
    {
        var file = State.Files.FirstOrDefault(x => x.Id == fileId) ?? throw new BadHttpRequestException("File not found.", 404);
        if (file.ProjectId is null)
        {
            if (file.UserId != userId) throw new BadHttpRequestException("File not found.", 404);
            return (file, userId, null);
        }

        var access = GetProjectAccess(userId, file.ProjectId) ?? throw new BadHttpRequestException("File not found.", 404);
        if (!permission(access)) throw new BadHttpRequestException("当前角色没有这个权限。", 403);
        return (file, access.OwnerId, access);
    }

    public IEnumerable<(Project Project, string Role)> AccessibleProjects(int userId)
    {
        var shared = State.ProjectMembers.Where(x => x.UserId == userId).ToDictionary(x => x.ProjectId, x => x.Role);
        foreach (var project in State.Projects)
        {
            if (project.UserId == userId) yield return (project, "owner");
            else if (shared.TryGetValue(project.Id, out var role)) yield return (project, role);
        }
    }

    // ---- Members and invitations ----

    static string NormalizeRole(string? role) =>
        ProjectAccess.MemberRoles.Contains(role?.Trim().ToLowerInvariant())
            ? role!.Trim().ToLowerInvariant()
            : throw new BadHttpRequestException("角色只能是 reviewer、annotator 或 viewer。", 400);

    public object ListMembers(ProjectAccess access)
    {
        var owner = GetUser(access.OwnerId);
        var members = State.ProjectMembers
            .Where(x => x.ProjectId == access.Project.Id)
            .Select(x => (Member: x, User: GetUser(x.UserId)))
            .Where(x => x.User is not null)
            .Select(x => new { userId = x.Member.UserId, email = x.User!.Email, username = x.User.Username, role = x.Member.Role, createdAt = x.Member.CreatedAt })
            .ToList();
        var invites = access.CanManage
            ? State.ProjectInvites.Where(x => x.ProjectId == access.Project.Id && x.ExpiresAt > DateTimeOffset.UtcNow)
                .Select(x => new { id = x.Id, email = x.Email, role = x.Role, createdAt = x.CreatedAt, expiresAt = x.ExpiresAt })
                .Cast<object>()
                .ToList()
            : [];
        return new
        {
            owner = owner is null ? null : new { userId = owner.Id, email = owner.Email, username = owner.Username, role = "owner" },
            members,
            invites,
            myRole = access.Role
        };
    }

    // Existing accounts join immediately. Unknown emails get a one-time invitation token, because
    // registration does not verify email ownership: matching on email alone would let anyone who
    // registers the invited address first take the seat.
    public async Task<(ProjectMember? Member, ProjectInvite? Invite, string? Token, bool Existing)> InviteMemberAsync(int actorId, ProjectAccess access, string email, string role)
    {
        role = NormalizeRole(role);
        email = (email ?? "").Trim().ToLowerInvariant();
        if (!email.Contains('@') || email.Length > 255) throw new BadHttpRequestException("请输入有效的邮箱。", 400);

        ProjectMember? member = null;
        ProjectInvite? invite = null;
        string? token = null;
        var existing = false;
        await SaveAsync(() =>
        {
            var user = State.Users.FirstOrDefault(x => x.Email == email);
            if (user is not null)
            {
                existing = true;
                if (user.Id == access.OwnerId) throw new BadHttpRequestException("项目所有者已经拥有全部权限。", 400);
                var key = ProjectMember.KeyFor(access.Project.Id, user.Id);
                State.ProjectMembers.RemoveAll(x => x.Id == key);
                member = new ProjectMember(key, access.Project.Id, user.Id, role, actorId, DateTimeOffset.UtcNow);
                State.ProjectMembers.Add(member);
                return;
            }

            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            State.ProjectInvites.RemoveAll(x => x.ProjectId == access.Project.Id && x.Email == email);
            invite = new ProjectInvite("inv_" + Util.Id(), access.Project.Id, email, role, Util.Sha256(token), actorId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));
            State.ProjectInvites.Add(invite);
        });
        return (member, invite, token, existing);
    }

    // Returns null when the invite was for a project the user already owns.
    public async Task<ProjectMember?> AcceptInviteAsync(User user, string token)
    {
        ProjectMember? member = null;
        var hash = Util.Sha256((token ?? "").Trim());
        await SaveAsync(() =>
        {
            var invite = State.ProjectInvites.FirstOrDefault(x => x.TokenHash == hash && x.ExpiresAt > DateTimeOffset.UtcNow)
                ?? throw new BadHttpRequestException("邀请链接无效或已过期。", 404);
            if (!invite.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase))
            {
                throw new BadHttpRequestException("这个邀请发给了另一个邮箱，请用被邀请的邮箱登录后再接受。", 403);
            }

            var project = State.Projects.FirstOrDefault(x => x.Id == invite.ProjectId) ?? throw new BadHttpRequestException("项目已不存在。", 404);
            State.ProjectInvites.Remove(invite);
            if (project.UserId == user.Id) return;
            var key = ProjectMember.KeyFor(project.Id, user.Id);
            State.ProjectMembers.RemoveAll(x => x.Id == key);
            member = new ProjectMember(key, project.Id, user.Id, invite.Role, invite.InvitedBy, DateTimeOffset.UtcNow);
            State.ProjectMembers.Add(member);
        });
        return member;
    }

    public async Task<bool> UpdateMemberRoleAsync(ProjectAccess access, int memberId, string role)
    {
        role = NormalizeRole(role);
        var updated = false;
        await SaveAsync(() =>
        {
            var key = ProjectMember.KeyFor(access.Project.Id, memberId);
            var member = State.ProjectMembers.FirstOrDefault(x => x.Id == key);
            if (member is null) return;
            State.ProjectMembers.Remove(member);
            State.ProjectMembers.Add(member with { Role = role });
            updated = true;
        });
        return updated;
    }

    // Removing a member also releases their image assignments.
    public async Task<bool> RemoveMemberAsync(string projectId, int memberId)
    {
        var removed = false;
        await SaveAsync(() =>
        {
            removed = State.ProjectMembers.RemoveAll(x => x.Id == ProjectMember.KeyFor(projectId, memberId)) > 0;
            var projectFiles = State.Files.Where(x => x.ProjectId == projectId).Select(x => x.Id).ToHashSet();
            State.FileAssignments.RemoveAll(x => x.UserId == memberId && projectFiles.Contains(x.FileId));
        });
        return removed;
    }

    public async Task<bool> RevokeInviteAsync(string projectId, string inviteId)
    {
        var removed = false;
        await SaveAsync(() => removed = State.ProjectInvites.RemoveAll(x => x.ProjectId == projectId && x.Id == inviteId) > 0);
        return removed;
    }

    // ---- Image assignment ----

    // Round-robin the selected images over the given members (empty list = unassign).
    public async Task<int> AssignFilesAsync(int actorId, ProjectAccess access, AssignmentRequest request)
    {
        var assignees = (request.UserIds ?? []).Distinct().ToList();
        foreach (var userId in assignees)
        {
            if (userId != access.OwnerId && !State.ProjectMembers.Any(x => x.ProjectId == access.Project.Id && x.UserId == userId && x.Role != "viewer"))
            {
                throw new BadHttpRequestException("只能分配给项目里可编辑的成员。", 400);
            }
        }

        var changed = 0;
        await SaveAsync(() =>
        {
            var projectFiles = State.Files.Where(x => x.ProjectId == access.Project.Id).OrderBy(x => x.Id).ToList();
            var assigned = State.FileAssignments.Select(x => x.FileId).ToHashSet();
            var annotated = State.AnnotationSets.Where(x => x.UserId == access.OwnerId).Select(x => x.FileId).ToHashSet();
            var targets = request.FileIds is { Count: > 0 }
                ? projectFiles.Where(x => request.FileIds.Contains(x.Id)).ToList()
                : request.Scope switch
                {
                    "all" => projectFiles,
                    "unannotated" => projectFiles.Where(x => !annotated.Contains(x.Id)).ToList(),
                    _ => projectFiles.Where(x => !assigned.Contains(x.Id)).ToList()
                };
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < targets.Count; i++)
            {
                State.FileAssignments.RemoveAll(x => x.FileId == targets[i].Id);
                if (assignees.Count > 0)
                {
                    State.FileAssignments.Add(new FileAssignment(targets[i].Id, assignees[i % assignees.Count], actorId, now));
                }

                changed++;
            }
        });
        return changed;
    }

    // ---- Review workflow ----

    public async Task<AnnotationSet> ReviewAnnotationAsync(int actorId, int ownerId, int fileId, string action, string? comment)
    {
        AnnotationSet? result = null;
        await SaveAsync(() =>
        {
            var set = State.AnnotationSets.FirstOrDefault(x => x.UserId == ownerId && x.FileId == fileId)
                ?? throw new BadHttpRequestException("这张图片还没有保存过标注。", 400);
            var now = DateTimeOffset.UtcNow;
            result = action switch
            {
                "submit" => set with { ReviewStatus = "submitted", SubmittedBy = actorId, UpdatedAt = now },
                // Approval is the human confirmation for every target that has a valid label.
                "approve" => set with
                {
                    ReviewStatus = "approved",
                    ReviewComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
                    ReviewedBy = actorId,
                    ReviewedAt = now,
                    UpdatedAt = now,
                    Annotations = set.Annotations.Select(x => x with { Confirmed = IsExportableAnnotation(x) }).ToList()
                },
                "reject" => set with
                {
                    ReviewStatus = "rejected",
                    ReviewComment = string.IsNullOrWhiteSpace(comment)
                        ? throw new BadHttpRequestException("驳回时请写明原因。", 400)
                        : comment!.Trim()[..Math.Min(comment!.Trim().Length, 1000)],
                    ReviewedBy = actorId,
                    ReviewedAt = now,
                    UpdatedAt = now,
                    Annotations = set.Annotations.Select(x => x with { Confirmed = false }).ToList()
                },
                _ => throw new BadHttpRequestException("Unknown review action.", 400)
            };
            State.AnnotationSets.Remove(set);
            State.AnnotationSets.Add(result);
        });
        return result!;
    }
}
