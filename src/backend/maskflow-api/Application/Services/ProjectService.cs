// Project operations on behalf of `userId`. Access is resolved here: the data owner id comes from
// the project, never from the caller, so members operate on the owner's data through their role.
public sealed class ProjectService
{
    readonly MaskFlowStore store;

    public ProjectService(MaskFlowStore store)
    {
        this.store = store;
    }

    public async Task<ProjectSummaryDto> CreateAsync(int userId, ProjectCreate request)
    {
        var project = await store.CreateProjectAsync(userId, request);
        return ToDto(project, "owner");
    }

    public IReadOnlyList<ProjectSummaryDto> List(int userId) =>
        store.AccessibleProjects(userId)
            .OrderByDescending(x => x.Project.UpdatedAt)
            .Select(x => ToDto(x.Project, x.Role))
            .ToList();

    public ProjectSummaryDto? Detail(int userId, string projectId) =>
        store.GetProjectAccess(userId, projectId) is { } access ? ToDto(access.Project, access.Role) : null;

    public async Task<ProjectSummaryDto?> UpdateAsync(int userId, string projectId, ProjectCreate request)
    {
        var access = store.RequireProjectAccess(userId, projectId, Permissions.Manage);
        var updated = await store.UpdateProjectAsync(access.OwnerId, projectId, request);
        return updated is null ? null : ToDto(updated, access.Role);
    }

    // A copy belongs to whoever makes it, so only the owner (whose quota pays) may copy.
    public async Task<ProjectSummaryDto?> CopyAsync(int userId, string projectId, ProjectCopyRequest request)
    {
        store.RequireProjectAccess(userId, projectId, Permissions.Manage);
        var copied = await store.CopyProjectAsync(userId, projectId, request);
        return copied is null ? null : ToDto(copied, "owner");
    }

    public Task<bool> DeleteAsync(int userId, string projectId)
    {
        store.RequireProjectAccess(userId, projectId, Permissions.Manage);
        return store.DeleteProjectAsync(userId, projectId);
    }

    public List<string> GetLabels(int userId, string projectId)
    {
        var access = store.RequireProjectAccess(userId, projectId, Permissions.Read);
        return store.GetProjectLabels(access.OwnerId, projectId);
    }

    public Task<List<string>> SaveLabelsAsync(int userId, string projectId, IEnumerable<string>? labels)
    {
        var access = store.RequireProjectAccess(userId, projectId, Permissions.Review);
        return store.SaveProjectLabelsAsync(access.OwnerId, projectId, labels);
    }

    public Task<List<string>> DeleteLabelAsync(int userId, string projectId, string labelName, string? replaceWith)
    {
        var access = store.RequireProjectAccess(userId, projectId, Permissions.Review);
        return store.DeleteProjectLabelAsync(access.OwnerId, projectId, labelName, replaceWith);
    }

    ProjectSummaryDto ToDto(Project project, string role)
    {
        var ownerId = project.UserId;
        var projectFiles = store.State.Files.Where(x => x.UserId == ownerId && x.ProjectId == project.Id).ToList();
        var fileIds = projectFiles.Select(x => x.Id).ToHashSet();
        var annotationCount = store.State.AnnotationSets.Where(x => x.UserId == ownerId && fileIds.Contains(x.FileId)).Sum(x => x.Annotations.Count);
        return new ProjectSummaryDto(
            project.Id,
            project.UserId,
            project.Name,
            project.Description,
            project.DataType,
            project.Split,
            projectFiles.Count,
            annotationCount,
            store.GetProjectLabels(ownerId, project.Id),
            project.CreatedAt,
            project.UpdatedAt,
            role,
            store.State.ProjectMembers.Count(x => x.ProjectId == project.Id));
    }
}
