using System.Text.Json;

public record MaskFlowState
{
    public int NextUserId { get; set; } = 1;
    public int NextFileId { get; set; } = 1;
    public int NextEventId { get; set; } = 1;
    public List<User> Users { get; set; } = [];
    public List<Session> Sessions { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<FileItem> Files { get; set; } = [];
    public List<AnnotationSet> AnnotationSets { get; set; } = [];
    public List<TaskItem> Tasks { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
    public List<JobEvent> JobEvents { get; set; } = [];
    public List<Node> Nodes { get; set; } = [];
    public List<Pool> Pools { get; set; } = [];
    public List<PricingRule> PricingRules { get; set; } = [];
    public List<WalletEntry> WalletLedger { get; set; } = [];
    public List<Settlement> Settlements { get; set; } = [];
    public List<ApiToken> ApiTokens { get; set; } = [];
    public List<TeamMember> TeamMembers { get; set; } = [];
    public List<AccountDevice> Devices { get; set; } = [];
    public List<DatasetExport> Exports { get; set; } = [];
    public List<AiModel> AiModels { get; set; } = [];
    public List<ProjectMember> ProjectMembers { get; set; } = [];
    public List<ProjectInvite> ProjectInvites { get; set; } = [];
    public List<FileAssignment> FileAssignments { get; set; } = [];
    public Dictionary<string, List<string>> ProjectLabels { get; set; } = [];
    // projectId -> label -> SAM 3 text prompt (labels without an entry use the label itself)
    public Dictionary<string, Dictionary<string, string>> ProjectLabelPrompts { get; set; } = [];
    public Dictionary<string, AiQuota> Quotas { get; set; } = [];
    public Dictionary<string, NotificationSettings> NotificationSettings { get; set; } = [];
}

public record RegisterRequest(string Email, string Password, string? Username);
public record LoginRequest(string Email, string Password);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);
public record ProfileRequest(string? Username, string? Phone);
public record PasswordChangeRequest(string CurrentPassword, string NewPassword);
public record SubscribeRequest(string Plan);
public record ApiTokenCreate(string Name);
public record TeamMemberCreate(string Email, string Role);
public record ProjectCreate(string Name, string? Description, string? DataType, SplitConfig? Split);
public record ProjectCopyRequest(string? Name);
public record ProjectLabelsRequest(List<string> Labels);
public record TaskCreate(string Type, string? Title, string? ProjectId, int? FileId, int ImageCount = 1);
public record ExportRequest(string? ProjectId, string? TaskId, SplitConfig? Split, string Format = "yolo", int Seed = 42, bool ConfirmedOnly = false, bool GroupNearDuplicates = true);
public record AnnotationAutoRequest(int FileId, double Conf = 0.25, string? DefaultLabel = null, string? Mode = null, string? ModelId = null);
public record AnnotationBatchRequest(string ProjectId, double Conf = 0.25, string? DefaultLabel = null, string? Mode = null, string? ModelId = null, string Scope = "unannotated");
public record LabelPromptsRequest(Dictionary<string, string?> Prompts);
public record ImportOptions(string? Format, bool Confirmed = true, string OnExisting = "skip", bool ImportImages = true);
public record ImportSummary(string Format, int ImagesAdded, int ImagesAnnotated, int AnnotationsImported, int SkippedExisting, List<string> NewLabels, List<string> UnmatchedAnnotations, List<string> Warnings);
public record AiModel(string Id, int UserId, string? ProjectId, string Name, string Path, long Size, string Sha256, List<string>? ClassNames, DateTimeOffset CreatedAt);
public record AnnotationPointsRequest(int FileId, List<double[]> Points, List<int> Labels, double Conf = 0.25);
public record AnnotationSaveRequest(int FileId, int Width, int Height, List<AnnotationItem> Annotations);
public record JobCreate(string Type, int UserId, string? ProjectId, Dictionary<string, object?> Input, Dictionary<string, object?> Params);
public record JobEventCreate(string EventType, Dictionary<string, object?> Payload, string? Status, double? Progress, string? Error);
public record NodeRegister(int OwnerId = 0, string Pool = "platform-gpu", string? GpuModel = null, int? VramGb = null, string? Region = null, double? PricePerHour = null);
public record NodeHeartbeat(string Status = "online", string? GpuModel = null, int? VramGb = null, string? Region = null);
public record PoolCreate(string? Id, string Name, string Type, string? Region, Dictionary<string, object?> Capacity, Dictionary<string, object?> Policy);
public record PricingCreate(string Name, string ResourceType, string? Pool, string? Region, double UnitPrice, string BillingUnit = "hour", string Status = "active");
public record SettlementCreate(int ProviderId, string Period, int NodeCount, double GrossAmount, double PlatformFee, string Status = "pending");
public record AuthResult(string Token, PublicUser User);
public record PublicUser(int Id, string Email, string Username, string Phone, string? AvatarUrl, string Plan, long QuotaBytes, long UsedBytes, long FreeBytes);
public record User(int Id, string Email, string PasswordHash, string Salt, string Plan, long QuotaBytes, long UsedBytes, DateTimeOffset CreatedAt, string? Username, string? Phone, string? AvatarPath);
public record Session(string Token, int UserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? DeviceId = null);
public record SplitConfig(int Train, int Val, int Test);
public record Project(string Id, int UserId, string Name, string Description, string DataType, SplitConfig Split, int ImageCount, int AnnotationCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
// Phash: 64-bit difference hash as 16 hex chars ("" = could not be computed, null = not yet computed).
public record FileItem(int Id, int UserId, string? ProjectId, string Name, string Path, long Size, string Kind, string? ContentType, DateTimeOffset CreatedAt, string DownloadUrl, string? Phash = null);
// Review: draft -> submitted (annotator) -> approved / rejected (reviewer). Solo projects stay "draft".
public record AnnotationSet(string Id, int UserId, int FileId, int Width, int Height, List<AnnotationItem> Annotations, string YoloTxt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string ReviewStatus = "draft", string? ReviewComment = null, int? ReviewedBy = null, DateTimeOffset? ReviewedAt = null, int? SubmittedBy = null);

// Project sharing: data and quota stay with the owner (Project.UserId); members act through their role.
public record ProjectMember(string Id, string ProjectId, int UserId, string Role, int InvitedBy, DateTimeOffset CreatedAt)
{
    public static string KeyFor(string projectId, int userId) => $"{projectId}:{userId}";
}
public record ProjectInvite(string Id, string ProjectId, string Email, string Role, string TokenHash, int InvitedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public record FileAssignment(int FileId, int UserId, int AssignedBy, DateTimeOffset AssignedAt);
public record MemberInviteRequest(string Email, string Role);
public record MemberRoleRequest(string Role);
public record AssignmentRequest(List<int>? UserIds, List<int>? FileIds, string Scope = "unassigned");
public record ReviewRequest(string? Comment);
public record AnnotationItem(string Id, int ClassId, string? Label, YoloBox Bbox, List<double>? Segment, double Confidence, bool Confirmed = false);
public record YoloBox(double Cx, double Cy, double Width, double Height);
public record TaskItem(string Id, int UserId, string JobId, string Type, string? Title, string? ProjectId, int? FileId, int ImageCount, string Status, double Progress, Dictionary<string, object?>? Result, string? ErrorMessage, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
public record DatasetExport(string Id, int UserId, string? ProjectId, string? TaskId, string Status, string? Path, long Size, ExportRequest Config, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, string? ErrorMessage, string? DownloadUrl);
public record AiQuota(string Plan, int DailyLimit, int DailyUsed, DateOnly DailyResetAt);
public record NotificationSettings(bool EmailTask, bool EmailBilling, bool BrowserNotice, bool WeeklyReport, DateTimeOffset UpdatedAt)
{
    public static NotificationSettings Default() => new(true, true, true, false, DateTimeOffset.UtcNow);
}
public record ApiToken(string Id, int UserId, string Name, string TokenHash, string TokenPrefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);
public record TeamMember(string Id, int UserId, string Email, string Role, string Status, DateTimeOffset CreatedAt);
public record AccountDevice(string Id, int UserId, string Name, string? Ip, string? UserAgent, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt);
public record Job(string Id, string App, string Type, int UserId, string? ProjectId, string Pool, string Priority, string Status, Dictionary<string, object?> Resources, Dictionary<string, object?> Input, Dictionary<string, object?>? Output, Dictionary<string, object?> Params, string? NodeId, int? ReservedCredits, int? ChargedCredits, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
public record JobEvent(int Id, string JobId, string EventType, Dictionary<string, object?> Payload, DateTimeOffset CreatedAt);
public record Node(string Id, int OwnerId, string Pool, string Status, string? GpuModel, int? VramGb, string? Region, double? PricePerHour, double Reputation, string ApiKey, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? LastHeartbeat)
{
    public object Public(string? apiKey = null) => new { Id, OwnerId, Pool, Status, GpuModel, VramGb, Region, PricePerHour, Reputation, CreatedAt, ApprovedAt, LastHeartbeat, ApiKey = apiKey };
}
public record Pool(string Id, string Name, string Type, string? Region, string Status, Dictionary<string, object?> Capacity, Dictionary<string, object?> Policy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public record PricingRule(string Id, string Name, string ResourceType, string? Pool, string? Region, double UnitPrice, string BillingUnit, string Status, DateTimeOffset EffectiveAt, DateTimeOffset UpdatedAt);
public record WalletEntry(int Id, int UserId, int Delta, string Reason, string? JobId, DateTimeOffset CreatedAt);
public record Settlement(string Id, int ProviderId, string Period, int NodeCount, double GrossAmount, double PlatformFee, double NetAmount, string Status, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt);
