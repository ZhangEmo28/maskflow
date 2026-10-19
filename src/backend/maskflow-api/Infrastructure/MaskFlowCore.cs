using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

public static class Util
{
    public static string Id() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class MaskFlowStore
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly IMaskFlowRepository repository;
    static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web);
    public string StorageRoot { get; } = Environment.GetEnvironmentVariable("MASKFLOW_STORAGE") ?? Path.Combine(AppContext.BaseDirectory, "data", "storage");
    string MinioEndpoint => Environment.GetEnvironmentVariable("MASKFLOW_MINIO_ENDPOINT") ?? "";
    string MinioAccessKey => Environment.GetEnvironmentVariable("MASKFLOW_MINIO_ACCESS_KEY") ?? "";
    string MinioSecretKey => Environment.GetEnvironmentVariable("MASKFLOW_MINIO_SECRET_KEY") ?? "";
    string MinioBucket => Environment.GetEnvironmentVariable("MASKFLOW_MINIO_BUCKET") ?? "maskflow";
    bool UseMinio => !string.IsNullOrWhiteSpace(MinioEndpoint);
    public MaskFlowState State { get; private set; } = new();
    readonly HashSet<string> pendingProjectLabelSync = new(StringComparer.Ordinal);
    readonly List<string> deleteAfterCommit = [];
    readonly List<string> deleteOnRollback = [];
    readonly Lazy<AmazonS3Client> s3Client;
    readonly ConcurrentDictionary<string, PasswordResetEntry> passwordResetTokens = new(StringComparer.OrdinalIgnoreCase);

    sealed record PasswordResetEntry(string TokenHash, DateTimeOffset ExpiresAt);

    public MaskFlowStore(IMaskFlowRepository repository)
    {
        this.repository = repository;
        s3Client = new Lazy<AmazonS3Client>(CreateS3Client, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public void Initialize()
    {
        Directory.CreateDirectory(StorageRoot);
        repository.EnsureSchemaAsync().GetAwaiter().GetResult();
        State = repository.LoadAsync().GetAwaiter().GetResult() ?? new MaskFlowState();
        EnsureMinioBucketAsync().GetAwaiter().GetResult();
        Seed();
        SyncTaskJobStatuses();
        SaveAsync().GetAwaiter().GetResult();
    }

    public Task SaveAsync() => SaveAsync(mutate: null);

    public Task SaveAsync(Action? mutate) =>
        SaveCoreAsync(mutate is null ? null : () =>
        {
            mutate();
            return Task.CompletedTask;
        });

    public Task SaveAsync(Func<Task>? mutateAsync) => SaveCoreAsync(mutateAsync);

    async Task SaveCoreAsync(Func<Task>? mutateAsync)
    {
        List<string> storageCleanup;
        Exception? failure = null;
        await gate.WaitAsync();
        var original = State;
        var working = CloneState(original);
        var originalPendingLabels = pendingProjectLabelSync.ToHashSet(StringComparer.Ordinal);
        try
        {
            State = working;
            if (mutateAsync is not null)
            {
                await mutateAsync();
            }

            await repository.SaveAsync(State, TakePendingProjectLabelSync());
            storageCleanup = [.. deleteAfterCommit];
        }
        catch (Exception ex)
        {
            State = original;
            pendingProjectLabelSync.Clear();
            pendingProjectLabelSync.UnionWith(originalPendingLabels);
            storageCleanup = [.. deleteOnRollback];
            failure = ex;
        }
        finally
        {
            deleteAfterCommit.Clear();
            deleteOnRollback.Clear();
            gate.Release();
        }

        // Object storage is not transactional: delete only after the database agrees,
        // and remove freshly written objects when the database write was rolled back.
        await DeleteStoredObjectsQuietlyAsync(storageCleanup);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    async Task DeleteStoredObjectsQuietlyAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                await DeleteStoredObjectAsync(path);
            }
            catch
            {
                // Best effort: an orphaned object only costs storage, never correctness.
            }
        }
    }

    static MaskFlowState CloneState(MaskFlowState state) =>
        JsonSerializer.Deserialize<MaskFlowState>(JsonSerializer.Serialize(state, StateJsonOptions), StateJsonOptions)
        ?? new MaskFlowState();

    IReadOnlyCollection<string>? TakePendingProjectLabelSync()
    {
        if (pendingProjectLabelSync.Count == 0)
        {
            return null;
        }

        var ids = pendingProjectLabelSync.ToList();
        pendingProjectLabelSync.Clear();
        return ids;
    }

    void MarkProjectLabelsDirty(string projectId) => pendingProjectLabelSync.Add(projectId);

    public async Task<T> MutateAsync<T>(Func<MaskFlowState, T> mutate)
    {
        T? result = default;
        await SaveAsync(() => result = mutate(State));
        return result!;
    }

    public async Task<T> MutateAsync<T>(Func<MaskFlowState, Task<T>> mutate)
    {
        T? result = default;
        await SaveAsync(async () => result = await mutate(State));
        return result!;
    }

    public Task MutateAsync(Func<MaskFlowState, Task> mutate) =>
        SaveAsync(async () => await mutate(State));

    AmazonS3Client CreateS3Client()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = MinioEndpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1"
        };
        return new AmazonS3Client(new BasicAWSCredentials(MinioAccessKey, MinioSecretKey), config);
    }

    async Task EnsureMinioBucketAsync()
    {
        if (!UseMinio) return;
        var client = s3Client.Value;
        var buckets = await client.ListBucketsAsync();
        if (buckets.Buckets.Any(x => x.BucketName == MinioBucket)) return;
        await client.PutBucketAsync(new PutBucketRequest { BucketName = MinioBucket });
    }

    public async Task<AuthResult?> RegisterAsync(string email, string password, string? username, HttpContext context)
    {
        email = email.Trim().ToLowerInvariant();
        if (State.Users.Any(x => x.Email == email))
        {
            return null;
        }

        // PBKDF2 is deliberately slow; never run it while holding the global write lock.
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var passwordHash = HashPassword(password, salt);
        AuthResult? result = null;
        await SaveAsync(() =>
        {
            if (State.Users.Any(x => x.Email == email))
            {
                return;
            }

            var user = new User(State.NextUserId++, email, passwordHash, salt, "free", 10L * 1024 * 1024 * 1024, 0, DateTimeOffset.UtcNow, username, "", null);
            State.Users.Add(user);
            State.Quotas[user.Id.ToString()] = new AiQuota("free", ResolveDailyLimit("free"), 0, DateOnly.FromDateTime(DateTime.UtcNow));
            State.TeamMembers.Add(new TeamMember("mem_owner_" + user.Id, user.Id, email, "owner", "active", DateTimeOffset.UtcNow));
            var deviceId = "dev_" + Util.Id();
            State.Devices.Add(new AccountDevice(deviceId, user.Id, "Current browser", context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
            var token = CreateSession(user.Id, deviceId);
            PruneExpiredSessions();
            result = new AuthResult(token, PublicUser(user));
        });
        return result;
    }

    public async Task<AuthResult?> LoginAsync(string email, string password, HttpContext context)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var candidate = State.Users.FirstOrDefault(x => x.Email == normalizedEmail);
        if (candidate is null)
        {
            // Spend the same PBKDF2 time as a real check so response timing does not reveal registered emails.
            VerifyPassword(password, DummyPasswordHash, DummyPasswordSalt);
            return null;
        }

        if (!VerifyPassword(password, candidate.PasswordHash, candidate.Salt))
        {
            return null;
        }

        AuthResult? result = null;
        await SaveAsync(() =>
        {
            var current = GetUser(candidate.Id);
            if (current is null || current.PasswordHash != candidate.PasswordHash)
            {
                return;
            }

            var deviceId = "dev_" + Util.Id();
            State.Devices.Add(new AccountDevice(deviceId, current.Id, "Current browser", context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
            var token = CreateSession(current.Id, deviceId);
            PruneExpiredSessions();
            result = new AuthResult(token, PublicUser(current));
        });
        return result;
    }

    public User RequireUser(HttpContext context) => OptionalUser(context) ?? throw new BadHttpRequestException("Missing bearer token.", 401);
    public User? OptionalUser(HttpContext context)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = auth["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;

        var session = State.Sessions.FirstOrDefault(x => x.Token == token);
        if (session is not null && session.ExpiresAt >= DateTimeOffset.UtcNow)
        {
            return GetUser(session.UserId);
        }

        if (token.StartsWith("mf_", StringComparison.Ordinal))
        {
            var hash = Util.Sha256(token);
            var apiToken = State.ApiTokens.FirstOrDefault(x => x.TokenHash == hash && x.RevokedAt is null);
            if (apiToken is not null)
            {
                return GetUser(apiToken.UserId);
            }
        }

        return null;
    }

    public User? GetUser(int id) => State.Users.FirstOrDefault(x => x.Id == id);

    public bool ValidateNodeKey(string nodeId, string plainKey)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(plainKey) || !plainKey.StartsWith("mf_", StringComparison.Ordinal))
        {
            return false;
        }

        var node = State.Nodes.FirstOrDefault(x => x.Id == nodeId);
        return node is not null && node.ApiKey == Util.Sha256(plainKey);
    }
    public PublicUser PublicUser(User user) => new(user.Id, user.Email, user.Username ?? "MaskFlow User", user.Phone ?? "", user.AvatarPath is null ? null : "/api/account/avatar", user.Plan, user.QuotaBytes, user.UsedBytes, Math.Max(0, user.QuotaBytes - user.UsedBytes));

    public Task UpdateProfileAsync(int userId, ProfileRequest request) =>
        SaveAsync(() =>
        {
            var user = GetUser(userId)!;
            State.Users.Remove(user);
            State.Users.Add(user with { Username = request.Username ?? user.Username, Phone = request.Phone ?? user.Phone });
        });

    public async Task<bool> ChangePasswordAsync(int userId, string currentPassword, string newPassword, string? keepSessionToken = null)
    {
        var user = GetUser(userId)!;
        if (!VerifyPassword(currentPassword, user.PasswordHash, user.Salt))
        {
            return false;
        }

        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var passwordHash = HashPassword(newPassword, salt);
        var changed = false;
        await SaveAsync(() =>
        {
            // Re-read inside the write lock: the user record may have changed (e.g. storage usage) meanwhile.
            var current = GetUser(userId);
            if (current is null)
            {
                return;
            }

            ReplaceUser(current with { Salt = salt, PasswordHash = passwordHash });
            InvalidateSessions(userId, keepSessionToken);
            changed = true;
        });
        return changed;
    }

    public string? CreatePasswordResetToken(string email)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = State.Users.FirstOrDefault(x => x.Email == normalizedEmail);
        if (user is null)
        {
            return null;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        passwordResetTokens[normalizedEmail] = new PasswordResetEntry(Util.Sha256(token), DateTimeOffset.UtcNow.AddMinutes(30));
        return token;
    }

    public async Task<bool> ResetPasswordWithTokenAsync(string email, string token, string newPassword)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            return false;
        }

        if (!passwordResetTokens.TryGetValue(normalizedEmail, out var entry)
            || entry.ExpiresAt < DateTimeOffset.UtcNow
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(entry.TokenHash),
                Encoding.ASCII.GetBytes(Util.Sha256(token.Trim()))))
        {
            return false;
        }

        var user = State.Users.FirstOrDefault(x => x.Email == normalizedEmail);
        if (user is null)
        {
            return false;
        }

        var changed = false;
        await SaveAsync(() =>
        {
            var current = State.Users.FirstOrDefault(x => x.Email == normalizedEmail);
            if (current is null)
            {
                return;
            }

            var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            State.Users.Remove(current);
            State.Users.Add(current with { Salt = salt, PasswordHash = HashPassword(newPassword, salt) });
            InvalidateSessions(current.Id, null);
            changed = true;
        });

        if (changed)
        {
            passwordResetTokens.TryRemove(normalizedEmail, out _);
        }

        return changed;
    }

    static int ResolveDailyLimit(string plan)
    {
        var overrideLimit = Environment.GetEnvironmentVariable("MASKFLOW_AI_DAILY_LIMIT");
        if (!string.IsNullOrWhiteSpace(overrideLimit) && int.TryParse(overrideLimit, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return plan switch { "pro" => 1000, "team" => 100000, _ => 50 };
    }

    public void EnsureAiQuotaAvailable(User user, int amount = 1)
    {
        var quota = GetQuota(user);
        if (quota.DailyUsed + amount > quota.DailyLimit)
        {
            throw new BadHttpRequestException(
                $"Daily AI quota exceeded. Used {quota.DailyUsed}/{quota.DailyLimit} today (UTC).",
                429);
        }
    }

    public Task UpdatePlanAsync(int userId, string plan) =>
        SaveAsync(() =>
        {
            var limit = ResolveDailyLimit(plan);
            var quotaBytes = plan switch { "pro" => 50L * 1024 * 1024 * 1024, "team" => 500L * 1024 * 1024 * 1024, _ => 10L * 1024 * 1024 * 1024 };
            var user = GetUser(userId)!;
            State.Users.Remove(user);
            State.Users.Add(user with { Plan = plan, QuotaBytes = quotaBytes });
            State.Quotas[userId.ToString()] = new AiQuota(plan, limit, 0, DateOnly.FromDateTime(DateTime.UtcNow));
        });

    // Reads never mutate shared state: a stale stored quota (new day / plan change) is
    // recomputed here and only written back when quota is actually consumed.
    public AiQuota GetQuota(User user)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var limit = ResolveDailyLimit(user.Plan);
        if (!State.Quotas.TryGetValue(user.Id.ToString(), out var quota) || quota.DailyResetAt < today)
        {
            return new AiQuota(user.Plan, limit, 0, today);
        }

        return quota.DailyLimit != limit || quota.Plan != user.Plan
            ? quota with { Plan = user.Plan, DailyLimit = limit }
            : quota;
    }

    public Task<AiQuota> GetQuotaAsync(User user) => Task.FromResult(GetQuota(user));

    public Task ConsumeAiQuotaAsync(int userId, int amount) =>
        SaveAsync(() => ApplyQuotaConsumption(userId, amount));

    void ApplyQuotaConsumption(int userId, int amount)
    {
        var user = GetUser(userId)!;
        var quota = GetQuota(user);
        if (quota.DailyUsed + amount > quota.DailyLimit) throw new BadHttpRequestException("Daily AI quota exceeded.", 429);
        State.Quotas[userId.ToString()] = quota with { DailyUsed = quota.DailyUsed + amount };
    }

    public async Task<FileItem> SaveUploadAsync(User user, IFormFile upload, string? projectId)
    {
        await UploadValidator.ValidateImageAsync(upload);

        FileItem? item = null;
        await SaveAsync(async () =>
        {
            projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId;
            if (projectId is not null && !State.Projects.Any(x => x.Id == projectId && x.UserId == user.Id))
            {
                throw new BadHttpRequestException("Project not found.", 404);
            }

            var currentUser = GetUser(user.Id)!;
            if (currentUser.UsedBytes + upload.Length > currentUser.QuotaBytes)
            {
                throw new BadHttpRequestException("Storage quota exceeded.", 403);
            }

            var safeName = Path.GetFileName(upload.FileName);
            var path = await PersistUploadAsync(upload, user.Id, projectId, safeName);
            deleteOnRollback.Add(path);
            var fileId = State.NextFileId++;
            item = new FileItem(fileId, currentUser.Id, projectId, safeName, path, upload.Length, "image", upload.ContentType, DateTimeOffset.UtcNow, $"/api/files/{fileId}/download");
            State.Files.Add(item);
            ReplaceUser(currentUser with { UsedBytes = currentUser.UsedBytes + upload.Length });
        });
        return item!;
    }

    async Task<string> PersistUploadAsync(IFormFile upload, int userId, string? projectId, string safeName)
    {
        var prefix = projectId is not null
            ? $"users/{userId}/projects/{projectId}/uploads"
            : $"users/{userId}/uploads";
        var objectKey = $"{prefix}/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Util.Id()}_{safeName}";
        if (UseMinio)
        {
            var client = s3Client.Value;
            await using var stream = upload.OpenReadStream();
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = MinioBucket,
                Key = objectKey,
                InputStream = stream,
                ContentType = upload.ContentType ?? "application/octet-stream"
            });
            return $"minio://{MinioBucket}/{objectKey}";
        }

        var userDir = projectId is not null
            ? Path.Combine(StorageRoot, userId.ToString(), "projects", projectId)
            : Path.Combine(StorageRoot, userId.ToString());
        Directory.CreateDirectory(userDir);
        var path = Path.Combine(userDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{safeName}");
        await using var target = File.Create(path);
        await upload.CopyToAsync(target);
        return path;
    }

    async Task<string> CopyStoredObjectAsync(string sourcePath, int userId, string projectId, string fileName, string? contentType)
    {
        var safeName = Path.GetFileName(fileName);
        var prefix = $"users/{userId}/projects/{projectId}/uploads";
        var objectKey = $"{prefix}/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Util.Id()}_{safeName}";
        if (UseMinio && sourcePath.StartsWith("minio://", StringComparison.OrdinalIgnoreCase))
        {
            var (sourceBucket, sourceKey) = ParseMinioPath(sourcePath);
            await s3Client.Value.CopyObjectAsync(sourceBucket, sourceKey, MinioBucket, objectKey);
            return $"minio://{MinioBucket}/{objectKey}";
        }

        if (UseMinio)
        {
            await using var sourceStream = await OpenStoredObjectAsync(sourcePath);
            await s3Client.Value.PutObjectAsync(new PutObjectRequest
            {
                BucketName = MinioBucket,
                Key = objectKey,
                InputStream = sourceStream,
                ContentType = contentType ?? "application/octet-stream"
            });
            return $"minio://{MinioBucket}/{objectKey}";
        }

        var userDir = Path.Combine(StorageRoot, userId.ToString(), "projects", projectId);
        Directory.CreateDirectory(userDir);
        var path = Path.Combine(userDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Util.Id()}_{safeName}");
        await using var source = await OpenStoredObjectAsync(sourcePath);
        await using var target = File.Create(path);
        await source.CopyToAsync(target);
        return path;
    }

    public async Task<Project> CreateProjectAsync(int userId, ProjectCreate request)
    {
        Project? project = null;
        await SaveAsync(() =>
        {
            project = new Project("proj_" + Util.Id(), userId, request.Name, request.Description ?? "", NormalizeDataType(request.DataType),
                request.Split ?? new SplitConfig(70, 20, 10), 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            State.Projects.Add(project);
            State.ProjectLabels[project.Id] = [];
            MarkProjectLabelsDirty(project.Id);
        });
        return project!;
    }

    public async Task<Project?> UpdateProjectAsync(int userId, string projectId, ProjectCreate request)
    {
        Project? updated = null;
        await SaveAsync(() =>
        {
            var project = State.Projects.FirstOrDefault(x => x.Id == projectId && x.UserId == userId);
            if (project is null)
            {
                return;
            }

            State.Projects.Remove(project);
            updated = project with
            {
                Name = request.Name,
                Description = request.Description ?? project.Description,
                DataType = request.DataType is null ? project.DataType : NormalizeDataType(request.DataType),
                Split = request.Split ?? project.Split,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            State.Projects.Add(updated);
        });
        return updated;
    }

    public async Task<Project?> CopyProjectAsync(int userId, string sourceProjectId, ProjectCopyRequest request)
    {
        Project? copied = null;
        await SaveAsync(async () =>
        {
            var source = State.Projects.FirstOrDefault(x => x.Id == sourceProjectId && x.UserId == userId);
            if (source is null) return;

            var sourceFiles = State.Files
                .Where(x => x.UserId == userId && x.ProjectId == sourceProjectId)
                .OrderBy(x => x.CreatedAt)
                .ToList();
            var currentUser = GetUser(userId)!;
            var copiedBytes = sourceFiles.Sum(x => x.Size);
            if (currentUser.UsedBytes + copiedBytes > currentUser.QuotaBytes)
            {
                throw new BadHttpRequestException("Storage quota exceeded.", 403);
            }

            var now = DateTimeOffset.UtcNow;
            var nextName = string.IsNullOrWhiteSpace(request.Name) ? $"{source.Name} 副本" : request.Name.Trim();
            copied = new Project("proj_" + Util.Id(), userId, nextName, source.Description, source.DataType, source.Split, 0, 0, now, now);
            State.Projects.Add(copied);

            var sourceLabels = GetProjectLabels(userId, sourceProjectId).ToList();
            State.ProjectLabels[copied.Id] = sourceLabels;
            MarkProjectLabelsDirty(copied.Id);

            var fileIdMap = new Dictionary<int, int>();
            foreach (var sourceFile in sourceFiles)
            {
                var copiedPath = await CopyStoredObjectAsync(sourceFile.Path, userId, copied.Id, sourceFile.Name, sourceFile.ContentType);
                deleteOnRollback.Add(copiedPath);
                var fileId = State.NextFileId++;
                var copiedFile = new FileItem(
                    fileId,
                    userId,
                    copied.Id,
                    sourceFile.Name,
                    copiedPath,
                    sourceFile.Size,
                    sourceFile.Kind,
                    sourceFile.ContentType,
                    sourceFile.CreatedAt,
                    $"/api/files/{fileId}/download");
                State.Files.Add(copiedFile);
                fileIdMap[sourceFile.Id] = fileId;
            }

            foreach (var sourceSet in State.AnnotationSets.Where(x => x.UserId == userId && fileIdMap.ContainsKey(x.FileId)).ToList())
            {
                var nextFileId = fileIdMap[sourceSet.FileId];
                var annotations = sourceSet.Annotations.Select(x => x with
                {
                    Bbox = x.Bbox with { },
                    Segment = x.Segment is null ? null : x.Segment.ToList()
                }).ToList();
                State.AnnotationSets.Add(new AnnotationSet(
                    "annset_" + Util.Id(),
                    userId,
                    nextFileId,
                    sourceSet.Width,
                    sourceSet.Height,
                    annotations,
                    BuildYoloTxt(annotations, copied.DataType),
                    now,
                    now));
            }

            ReplaceUser(currentUser with { UsedBytes = currentUser.UsedBytes + copiedBytes });
        });
        return copied;
    }

    public async Task<bool> DeleteProjectAsync(int userId, string projectId)
    {
        var deleted = false;
        await SaveAsync(() =>
        {
            var project = State.Projects.FirstOrDefault(x => x.Id == projectId && x.UserId == userId);
            if (project is null)
            {
                return;
            }

            var fileIds = State.Files.Where(x => x.UserId == userId && x.ProjectId == projectId).Select(x => x.Id).ToList();
            foreach (var fileId in fileIds)
            {
                DeleteFileCoreLocked(userId, fileId);
            }

            State.Tasks.RemoveAll(x => x.UserId == userId && x.ProjectId == projectId);
            foreach (var export in State.Exports.Where(x => x.UserId == userId && x.ProjectId == projectId && x.Path is not null))
            {
                deleteAfterCommit.Add(export.Path!);
            }

            State.Exports.RemoveAll(x => x.UserId == userId && x.ProjectId == projectId);
            MarkProjectLabelsDirty(projectId);
            State.ProjectLabels.Remove(projectId);
            State.Projects.Remove(project);
            deleted = true;
        });
        return deleted;
    }

    public async Task<bool> DeleteFileAsync(int userId, int fileId)
    {
        var deleted = false;
        await SaveAsync(() => { deleted = DeleteFileCoreLocked(userId, fileId); });
        return deleted;
    }

    bool DeleteFileCoreLocked(int userId, int fileId)
    {
        var file = State.Files.FirstOrDefault(x => x.Id == fileId && x.UserId == userId);
        if (file is null) return false;
        State.Files.Remove(file);
        State.AnnotationSets.RemoveAll(x => x.FileId == fileId && x.UserId == userId);
        deleteAfterCommit.Add(file.Path);

        var user = GetUser(userId);
        if (user is not null)
        {
            ReplaceUser(user with { UsedBytes = Math.Max(0, user.UsedBytes - file.Size) });
        }

        return true;
    }

    public async Task<AnnotationSet> SaveAnnotationSetAsync(int userId, AnnotationSaveRequest request)
    {
        AnnotationSet? set = null;
        await SaveAsync(() => set = ApplyAnnotationSet(userId, request));
        return set!;
    }

    AnnotationSet ApplyAnnotationSet(int userId, AnnotationSaveRequest request)
    {
        var file = State.Files.FirstOrDefault(x => x.Id == request.FileId && x.UserId == userId);
        if (file is null) throw new BadHttpRequestException("File not found.", 404);

        var projectLabels = file.ProjectId is not null ? GetProjectLabels(userId, file.ProjectId) : null;
        var dataType = ResolveProjectDataType(userId, file.ProjectId);
        var annotations = NormalizeAnnotations(request.Annotations, projectLabels);
        var existing = State.AnnotationSets.FirstOrDefault(x => x.FileId == request.FileId && x.UserId == userId);
        if (existing is not null)
        {
            State.AnnotationSets.Remove(existing);
        }

        var now = DateTimeOffset.UtcNow;
        var set = new AnnotationSet(
            existing?.Id ?? "annset_" + Util.Id(),
            userId,
            request.FileId,
            request.Width,
            request.Height,
            annotations,
            BuildYoloTxt(annotations, dataType),
            existing?.CreatedAt ?? now,
            now);
        State.AnnotationSets.Add(set);
        return set;
    }

    public async Task<(AnnotationSet Annotation, TaskItem Task)> PersistAutoAnnotationAsync(int userId, AnnotationSaveRequest request, FileItem file, int quotaAmount = 1)
    {
        AnnotationSet? set = null;
        TaskItem? completed = null;
        await SaveAsync(() =>
        {
            var existing = GetAnnotationSet(userId, file.Id);
            var merged = AnnotationMerge.KeepConfirmed(existing?.Annotations, request.Annotations);
            set = ApplyAnnotationSet(userId, request with { Annotations = merged });
            var task = CreateTaskCore(userId, "yolo_auto_annotation", $"自动标注 {file.Name}", file.ProjectId, file.Id, 1);
            completed = ApplyTaskUpdate(userId, task.Id, "completed", 1, null, new Dictionary<string, object?>
            {
                ["fileId"] = file.Id,
                ["annotationCount"] = set.Annotations.Count,
                ["keptConfirmed"] = merged.Count(x => x.Confirmed)
            });
            ApplyQuotaConsumption(userId, quotaAmount);
        });
        return (set!, completed!);
    }

    public AnnotationSet? GetAnnotationSet(int userId, int fileId) => State.AnnotationSets.FirstOrDefault(x => x.UserId == userId && x.FileId == fileId);

    public List<string> GetProjectLabels(int userId, string projectId)
    {
        var project = State.Projects.FirstOrDefault(x => x.Id == projectId && x.UserId == userId);
        if (project is null) throw new BadHttpRequestException("Project not found.", 404);
        if (!State.ProjectLabels.TryGetValue(projectId, out var labels) || labels.Count == 0)
        {
            return [];
        }

        return labels.ToList();
    }

    public async Task<List<string>> SaveProjectLabelsAsync(int userId, string projectId, IEnumerable<string>? labels)
    {
        List<string>? cleaned = null;
        await SaveAsync(() => cleaned = ApplyProjectLabels(userId, projectId, labels));
        return cleaned!;
    }

    List<string> ApplyProjectLabels(int userId, string projectId, IEnumerable<string>? labels)
    {
        var project = State.Projects.FirstOrDefault(x => x.Id == projectId && x.UserId == userId);
        if (project is null) throw new BadHttpRequestException("Project not found.", 404);
        var cleaned = (labels ?? [])
            .Select(x => string.IsNullOrWhiteSpace(x) ? "" : x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        State.ProjectLabels[projectId] = cleaned;
        MarkProjectLabelsDirty(projectId);
        return cleaned;
    }

    public async Task<List<string>> DeleteProjectLabelAsync(int userId, string projectId, string labelName, string? replaceWith = null)
    {
        List<string>? cleaned = null;
        await SaveAsync(() =>
        {
            var labels = GetProjectLabels(userId, projectId).ToList();
            if (!labels.Any(x => x.Equals(labelName.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new BadHttpRequestException("Label not found in project.", 404);
            }

            labels.RemoveAll(x => x.Equals(labelName.Trim(), StringComparison.OrdinalIgnoreCase));
            cleaned = ApplyProjectLabels(userId, projectId, labels);

            string? replacement = string.IsNullOrWhiteSpace(replaceWith) ? null : replaceWith.Trim();
            if (replacement is not null && ResolveLabelClassId(replacement, cleaned) < 0)
            {
                throw new BadHttpRequestException("replaceWith is not in project labels.", 400);
            }

            var fileIds = State.Files
                .Where(x => x.UserId == userId && x.ProjectId == projectId)
                .Select(x => x.Id)
                .ToHashSet();
            foreach (var set in State.AnnotationSets.Where(x => x.UserId == userId && fileIds.Contains(x.FileId)).ToList())
            {
                var nextAnnotations = set.Annotations
                    .Select(item => item.Label is not null && item.Label.Equals(labelName, StringComparison.OrdinalIgnoreCase)
                        ? item with { Label = replacement }
                        : item)
                    .ToList();
                var normalized = NormalizeAnnotations(nextAnnotations, cleaned);
                State.AnnotationSets.Remove(set);
                State.AnnotationSets.Add(set with
                {
                    Annotations = normalized,
                    YoloTxt = BuildYoloTxt(normalized, ResolveProjectDataType(userId, projectId)),
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        });
        return cleaned!;
    }

    public async Task<NotificationSettings> UpdateNotificationSettingsAsync(int userId, NotificationSettings request)
    {
        NotificationSettings? settings = null;
        await SaveAsync(() =>
        {
            settings = request with { UpdatedAt = DateTimeOffset.UtcNow };
            State.NotificationSettings[userId.ToString()] = settings;
        });
        return settings!;
    }

    public async Task<(ApiToken Token, string PlainValue)> CreateApiTokenAsync(int userId, string name)
    {
        var plain = "mf_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        ApiToken? token = null;
        await SaveAsync(() =>
        {
            token = new ApiToken("tok_" + Util.Id(), userId, name, Util.Sha256(plain), plain[..8], DateTimeOffset.UtcNow, null, null);
            State.ApiTokens.Add(token);
        });
        return (token!, plain);
    }

    public async Task<bool> RevokeApiTokenAsync(int userId, string tokenId)
    {
        var revoked = false;
        await SaveAsync(() =>
        {
            var token = State.ApiTokens.FirstOrDefault(x => x.Id == tokenId && x.UserId == userId);
            if (token is null)
            {
                return;
            }

            State.ApiTokens.Remove(token);
            State.ApiTokens.Add(token with { RevokedAt = DateTimeOffset.UtcNow });
            revoked = true;
        });
        return revoked;
    }

    public async Task<TeamMember> AddTeamMemberAsync(int userId, TeamMemberCreate request)
    {
        TeamMember? member = null;
        await SaveAsync(() =>
        {
            member = new TeamMember("mem_" + Util.Id(), userId, request.Email, request.Role, "invited", DateTimeOffset.UtcNow);
            State.TeamMembers.Add(member);
        });
        return member!;
    }

    public async Task<bool> RemoveTeamMemberAsync(int userId, string memberId)
    {
        var removed = false;
        await SaveAsync(() =>
        {
            var member = State.TeamMembers.FirstOrDefault(x => x.Id == memberId && x.UserId == userId);
            if (member is null)
            {
                return;
            }

            if (member.Role == "owner") throw new BadHttpRequestException("Owner cannot be removed.", 400);
            State.TeamMembers.Remove(member);
            removed = true;
        });
        return removed;
    }

    public async Task<AccountDevice?> RevokeDeviceAsync(int userId, string deviceId)
    {
        AccountDevice? revoked = null;
        await SaveAsync(() =>
        {
            var device = State.Devices.FirstOrDefault(x => x.Id == deviceId && x.UserId == userId);
            if (device is null)
            {
                return;
            }

            State.Devices.Remove(device);
            revoked = device with { RevokedAt = DateTimeOffset.UtcNow };
            State.Devices.Add(revoked);
            InvalidateSessionsForDevice(userId, deviceId);
        });
        return revoked;
    }

    public async Task<Node> RegisterNodeAsync(NodeRegister request, string apiKey)
    {
        Node? node = null;
        await SaveAsync(() =>
        {
            node = new Node("node_" + Util.Id(), request.OwnerId, request.Pool, "pending", request.GpuModel, request.VramGb, request.Region, request.PricePerHour, 0, Util.Sha256(apiKey), DateTimeOffset.UtcNow, null, null);
            State.Nodes.Add(node);
        });
        return node!;
    }

    public async Task<Job> AddJobAsync(JobCreate request)
    {
        Job? job = null;
        await SaveAsync(() =>
        {
            job = new Job("job_" + Util.Id(), "maskflow", request.Type, request.UserId, request.ProjectId, "platform-gpu", "normal", "queued",
                new Dictionary<string, object?> { ["gpu"] = 1 }, request.Input, null, request.Params, null, null, null, null, DateTimeOffset.UtcNow, null, null);
            State.Jobs.Add(job);
        });
        return job!;
    }

    public async Task<Pool> AddPoolAsync(PoolCreate request)
    {
        Pool? pool = null;
        await SaveAsync(() =>
        {
            pool = new Pool(request.Id ?? "pool_" + Util.Id(), request.Name, request.Type, request.Region, "active", request.Capacity, request.Policy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            State.Pools.Add(pool);
        });
        return pool!;
    }

    public async Task<PricingRule> AddPricingRuleAsync(PricingCreate request)
    {
        PricingRule? rule = null;
        await SaveAsync(() =>
        {
            rule = new PricingRule("price_" + Util.Id(), request.Name, request.ResourceType, request.Pool, request.Region, request.UnitPrice, request.BillingUnit, request.Status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            State.PricingRules.Add(rule);
        });
        return rule!;
    }

    public async Task<Settlement> AddSettlementAsync(SettlementCreate request)
    {
        Settlement? settlement = null;
        await SaveAsync(() =>
        {
            settlement = new Settlement("settle_" + Util.Id(), request.ProviderId, request.Period, request.NodeCount, request.GrossAmount, request.PlatformFee, request.GrossAmount - request.PlatformFee, request.Status, DateTimeOffset.UtcNow, null);
            State.Settlements.Add(settlement);
        });
        return settlement!;
    }

    public static bool IsExportableAnnotation(AnnotationItem annotation) =>
        !string.IsNullOrWhiteSpace(annotation.Label) && annotation.ClassId >= 0;

    public static int ResolveLabelClassId(string? label, IReadOnlyList<string>? projectLabels)
    {
        if (string.IsNullOrWhiteSpace(label) || projectLabels is null || projectLabels.Count == 0)
        {
            return -1;
        }

        var trimmed = label.Trim();
        for (var i = 0; i < projectLabels.Count; i++)
        {
            if (projectLabels[i].Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public static List<AnnotationItem> NormalizeAnnotations(List<AnnotationItem>? annotations, IReadOnlyList<string>? projectLabels = null)
    {
        var normalized = new List<AnnotationItem>();
        foreach (var annotation in annotations ?? [])
        {
            var label = string.IsNullOrWhiteSpace(annotation.Label) ? null : annotation.Label.Trim();
            var classId = ResolveLabelClassId(label, projectLabels);
            if (label is not null && classId < 0)
            {
                label = null;
                classId = -1;
            }
            else if (classId >= 0)
            {
                label = projectLabels![classId];
            }

            var box = annotation.Bbox;
            normalized.Add(annotation with
            {
                Id = string.IsNullOrWhiteSpace(annotation.Id) ? "ann_" + Util.Id() : annotation.Id,
                ClassId = classId,
                Label = label,
                Bbox = new YoloBox(Clamp01(box.Cx), Clamp01(box.Cy), Clamp01(box.Width), Clamp01(box.Height)),
                Segment = annotation.Segment?.Select(Clamp01).ToList(),
                Confidence = annotation.Confidence <= 0 ? 1.0 : annotation.Confidence,
                Confirmed = annotation.Confirmed
            });
        }

        return normalized;
    }

    public static string NormalizeDataType(string? dataType) =>
        string.Equals(dataType, "segmentation", StringComparison.OrdinalIgnoreCase) ? "segmentation" : "detection";

    public static string ResolveYoloTask(string? dataType) =>
        NormalizeDataType(dataType) == "segmentation" ? "segment" : "detect";

    string ResolveProjectDataType(int userId, string? projectId)
    {
        if (projectId is null)
        {
            return "detection";
        }

        var project = State.Projects.FirstOrDefault(x => x.Id == projectId && x.UserId == userId);
        return NormalizeDataType(project?.DataType);
    }

    public static bool HasValidSegment(AnnotationItem annotation)
    {
        var points = annotation.Segment;
        if (points is not { Count: >= 6 } || points.Count % 2 != 0
            || points.Any(x => !double.IsFinite(x) || x < 0 || x > 1)) return false;
        double area = 0;
        for (var i = 0; i < points.Count; i += 2)
        {
            var next = (i + 2) % points.Count;
            area += points[i] * points[next + 1] - points[next] * points[i + 1];
        }
        return Math.Abs(area) > 1e-12;
    }

    public static string BuildYoloLine(AnnotationItem annotation, string? dataType)
    {
        if (NormalizeDataType(dataType) == "segmentation")
        {
            if (!HasValidSegment(annotation))
                throw new BadHttpRequestException("目标缺少有效分割轮廓，请补充分割或改用检测格式。", 400);
            return $"{annotation.ClassId} {string.Join(" ", annotation.Segment!.Select(FormatYolo))}";
        }

        var box = annotation.Bbox;
        return $"{annotation.ClassId} {FormatYolo(box.Cx)} {FormatYolo(box.Cy)} {FormatYolo(box.Width)} {FormatYolo(box.Height)}";
    }

    public static string BuildYoloTxt(IEnumerable<AnnotationItem> annotations, string? dataType = "detection") =>
        string.Join("\n", annotations.Where(IsExportableAnnotation)
            .Where(x => NormalizeDataType(dataType) != "segmentation" || HasValidSegment(x))
            .Select(annotation => BuildYoloLine(annotation, dataType)));

    public async Task<DatasetExport> CreateDatasetExportAsync(int userId, ExportRequest request)
    {
        var split = request.Split ?? new SplitConfig(70, 20, 10);
        DatasetSplitter.Validate(split);

        DatasetExport? export = null;
        await SaveAsync(async () =>
        {
            var plan = DatasetExportPlan.Create(State, userId, request);
            plan.ValidateExport();
            var exportId = "export_" + Util.Id();
            var exportDir = Path.Combine(StorageRoot, userId.ToString(), "exports");
            Directory.CreateDirectory(exportDir);
            var zipPath = Path.Combine(exportDir, $"{exportId}.zip");
            deleteOnRollback.Add(zipPath);
            var annotationMap = plan.Annotations;
            var labeledFiles = plan.Files;
            var exportFormat = plan.Format;
            var exportDataType = plan.DataType;
            var yoloTask = ResolveYoloTask(exportDataType);

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var labels = plan.Labels;
                var samples = plan.Samples;
                var assignments = DatasetSplitter.Assign(samples, split, request.Seed);

                if (exportFormat == "classification-crops")
                {
                    await WriteClassificationCropDatasetAsync(archive, labeledFiles, annotationMap, labels, assignments);
                }
                else
                {
                    for (var index = 0; index < labeledFiles.Count; index++)
                    {
                        var file = labeledFiles[index];
                        var targetSplit = assignments[file.Id];
                        var extension = Path.GetExtension(file.Name);
                        var stem = SanitizeFileName($"{file.Id}_{Path.GetFileNameWithoutExtension(file.Name)}");
                        var imageEntry = archive.CreateEntry($"images/{targetSplit}/{stem}{extension}");
                        await using (var imageEntryStream = imageEntry.Open())
                        await using (var imageStream = await OpenStoredObjectAsync(file.Path))
                        {
                            await imageStream.CopyToAsync(imageEntryStream);
                        }

                        var labelEntry = archive.CreateEntry($"labels/{targetSplit}/{stem}.txt");
                        using var labelWriter = new StreamWriter(labelEntry.Open());
                        labelWriter.Write(BuildYoloTxt(annotationMap[file.Id].Annotations, exportDataType));
                    }

                    var dataEntry = archive.CreateEntry("data.yaml");
                    using (var writer = new StreamWriter(dataEntry.Open()))
                    {
                        writer.WriteLine("path: .");
                        writer.WriteLine($"task: {yoloTask}");
                        writer.WriteLine("train: images/train");
                        writer.WriteLine("val: images/val");
                        writer.WriteLine("test: images/test");
                        writer.WriteLine($"nc: {labels.Count}");
                        writer.WriteLine("names:");
                        for (var i = 0; i < labels.Count; i++)
                        {
                            writer.WriteLine($"  {i}: {JsonSerializer.Serialize(labels[i])}");
                        }
                    }
                }

                var reportEntry = archive.CreateEntry("split-report.json");
                await using (var reportStream = reportEntry.Open())
                    await JsonSerializer.SerializeAsync(reportStream,
                        DatasetSplitter.Report(samples, labels, assignments, split, request.Seed),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

                var readme = archive.CreateEntry("README.md");
                using var readmeWriter = new StreamWriter(readme.Open());
                readmeWriter.WriteLine(exportFormat == "classification-crops" ? "# MaskFlow Classification Crop Dataset" : "# MaskFlow YOLO Dataset");
                readmeWriter.WriteLine();
                readmeWriter.WriteLine($"Format: {exportFormat}");
                readmeWriter.WriteLine($"Task: {(exportFormat == "classification-crops" ? "classify" : yoloTask)}");
                readmeWriter.WriteLine($"DataType: {exportDataType}");
                readmeWriter.WriteLine($"Images: {labeledFiles.Count}");
                readmeWriter.WriteLine($"Split: stratified by label, grouped by source image; seed={request.Seed}");
                readmeWriter.WriteLine("See split-report.json for actual per-class image/annotation counts, file assignments and coverage warnings.");
                readmeWriter.WriteLine($"GeneratedAt: {DateTimeOffset.UtcNow:O}");
            }

            var zipSize = new FileInfo(zipPath).Length;
            var exportPath = zipPath;
            if (UseMinio)
            {
                var exportKey = $"users/{userId}/exports/{exportId}.zip";
                await using (var zipStream = File.OpenRead(zipPath))
                {
                    await s3Client.Value.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = MinioBucket,
                        Key = exportKey,
                        InputStream = zipStream,
                        ContentType = "application/zip"
                    });
                }
                exportPath = $"minio://{MinioBucket}/{exportKey}";
                deleteOnRollback.Add(exportPath);
                File.Delete(zipPath);
            }

            export = new DatasetExport(exportId, userId, request.ProjectId, request.TaskId, "completed", exportPath, zipSize, request with { Format = exportFormat, Split = split }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, $"/api/export/{exportId}/download");
            State.Exports.Add(export);
        });
        return export!;
    }

    async Task WriteClassificationCropDatasetAsync(
        ZipArchive archive,
        IReadOnlyList<FileItem> labeledFiles,
        IReadOnlyDictionary<int, AnnotationSet> annotationMap,
        IReadOnlyList<string> labels,
        IReadOnlyDictionary<int, string> assignments)
    {
        var samples = labeledFiles
            .SelectMany(file => annotationMap[file.Id].Annotations
                .Where(IsExportableAnnotation)
                .Select(annotation => new { File = file, Annotation = annotation }))
            .ToList();

        if (samples.Count == 0)
        {
            throw new BadHttpRequestException("No labeled annotations found for classification export.", 400);
        }

        var classEntry = archive.CreateEntry("classes.txt");
        using (var classWriter = new StreamWriter(classEntry.Open()))
        {
            foreach (var label in labels) classWriter.WriteLine(label);
        }

        var exported = 0;
        var grouped = samples.GroupBy(x => x.File.Id).ToDictionary(x => x.Key, x => x.ToList());
        foreach (var (fileId, fileSamples) in grouped)
        {
            var file = fileSamples[0].File;
            await using var imageStream = await OpenStoredObjectAsync(file.Path);
            using var image = await ImageTools.DecodeOrientedAsync(imageStream);
            foreach (var sample in fileSamples)
            {
                var cropRect = ImageTools.CropRectangle(sample.Annotation.Bbox, image.Width, image.Height);
                if (cropRect.Width <= 0 || cropRect.Height <= 0) continue;

                var targetSplit = assignments[fileId];
                var labelDir = SanitizeFileName(sample.Annotation.Label ?? "unassigned");
                var stem = SanitizeFileName($"{fileId}_{Path.GetFileNameWithoutExtension(file.Name)}_{sample.Annotation.Id}");
                var cropEntry = archive.CreateEntry($"classification/{targetSplit}/{labelDir}/{stem}.jpg");
                var jpeg = ImageTools.EncodeJpegCrop(image, cropRect);
                await using var cropStream = cropEntry.Open();
                await cropStream.WriteAsync(jpeg);
                exported += 1;
            }
        }

        if (exported == 0)
        {
            throw new BadHttpRequestException("No valid annotation crops found for classification export.", 400);
        }
    }

    public async Task<Stream> OpenStoredObjectAsync(string path)
    {
        if (path.StartsWith("minio://", StringComparison.OrdinalIgnoreCase))
        {
            var (bucket, key) = ParseMinioPath(path);
            var response = await s3Client.Value.GetObjectAsync(bucket, key);
            // Stream straight from MinIO instead of buffering whole objects (exports can be GBs).
            return new S3ObjectStream(response);
        }

        return File.OpenRead(path);
    }

    public async Task<bool> StoredObjectExistsAsync(string path)
    {
        if (path.StartsWith("minio://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (bucket, key) = ParseMinioPath(path);
                var client = s3Client.Value;
                await client.GetObjectMetadataAsync(bucket, key);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return File.Exists(path);
    }

    async Task DeleteStoredObjectAsync(string path)
    {
        if (path.StartsWith("minio://", StringComparison.OrdinalIgnoreCase))
        {
            var (bucket, key) = ParseMinioPath(path);
            var client = s3Client.Value;
            await client.DeleteObjectAsync(bucket, key);
            return;
        }

        if (File.Exists(path)) File.Delete(path);
    }

    static (string Bucket, string Key) ParseMinioPath(string path)
    {
        var withoutScheme = path["minio://".Length..];
        var slash = withoutScheme.IndexOf('/');
        if (slash < 0) return (withoutScheme, "");
        return (withoutScheme[..slash], withoutScheme[(slash + 1)..]);
    }

    public async Task<Job?> SetJobStatusAsync(string jobId, string status)
    {
        Job? result = null;
        await SaveAsync(() =>
        {
            var job = State.Jobs.FirstOrDefault(x => x.Id == jobId);
            if (job is null)
            {
                return;
            }

            State.Jobs.Remove(job);
            result = job with { Status = status, FinishedAt = status is "cancelled" or "succeeded" or "failed" ? DateTimeOffset.UtcNow : null };
            State.Jobs.Add(result);
        });
        return result;
    }

    public async Task<object?> AddJobEventAsync(string jobId, JobEventCreate request)
    {
        object? result = null;
        await SaveAsync(() =>
        {
            var job = State.Jobs.FirstOrDefault(x => x.Id == jobId);
            if (job is null)
            {
                return;
            }

            var ev = new JobEvent(State.NextEventId++, jobId, request.EventType, request.Payload, DateTimeOffset.UtcNow);
            State.JobEvents.Add(ev);
            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                State.Jobs.Remove(job);
                State.Jobs.Add(job with { Status = request.Status, Error = request.Error ?? job.Error, FinishedAt = request.Status is "succeeded" or "failed" or "cancelled" ? DateTimeOffset.UtcNow : null });
            }

            result = new { @event = ev, job = State.Jobs.First(x => x.Id == jobId) };
        });
        return result;
    }

    public async Task<Node?> HeartbeatNodeAsync(string nodeId, NodeHeartbeat request)
    {
        Node? result = null;
        await SaveAsync(() =>
        {
            var node = State.Nodes.FirstOrDefault(x => x.Id == nodeId);
            if (node is null)
            {
                return;
            }

            State.Nodes.Remove(node);
            result = node with { Status = request.Status, GpuModel = request.GpuModel ?? node.GpuModel, VramGb = request.VramGb ?? node.VramGb, Region = request.Region ?? node.Region, LastHeartbeat = DateTimeOffset.UtcNow };
            State.Nodes.Add(result);
        });
        return result;
    }

    public async Task<Node?> NodeStatusAsync(string nodeId, string status, bool approve = false)
    {
        Node? result = null;
        await SaveAsync(() =>
        {
            var node = State.Nodes.FirstOrDefault(x => x.Id == nodeId);
            if (node is null)
            {
                return;
            }

            State.Nodes.Remove(node);
            result = node with { Status = status, ApprovedAt = approve ? DateTimeOffset.UtcNow : node.ApprovedAt, LastHeartbeat = DateTimeOffset.UtcNow };
            State.Nodes.Add(result);
        });
        return result;
    }

    public async Task<(bool NodeFound, Job? Job)> PollJobAsync(string nodeId)
    {
        var nodeFound = false;
        Job? result = null;
        await SaveAsync(() =>
        {
            var node = State.Nodes.FirstOrDefault(x => x.Id == nodeId);
            if (node is null)
            {
                return;
            }

            nodeFound = true;
            if (node.ApprovedAt is null || node.Status is "disabled" or "suspended")
            {
                return;
            }

            var job = State.Jobs
                .Where(x => x.Status == "queued" && x.Pool == node.Pool)
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefault();
            if (job is null)
            {
                return;
            }

            State.Jobs.Remove(job);
            result = job with { Status = "running", NodeId = nodeId, StartedAt = DateTimeOffset.UtcNow };
            State.Jobs.Add(result);
        });
        return (nodeFound, result);
    }

    public async Task<TaskItem> CreateTaskAsync(int userId, string type, string? title, string? projectId, int? fileId, int imageCount)
    {
        TaskItem? task = null;
        await SaveAsync(() => task = CreateTaskCore(userId, type, title, projectId, fileId, imageCount));
        return task!;
    }

    TaskItem CreateTaskCore(int userId, string type, string? title, string? projectId, int? fileId, int imageCount)
    {
        var job = CreateJobCore(type.Replace("auto_", "sam."), userId, projectId, new Dictionary<string, object?>(), new Dictionary<string, object?>());
        var task = new TaskItem("task_" + Util.Id(), userId, job.Id, type, title ?? type, projectId, fileId, imageCount, "queued", 0, null, null, DateTimeOffset.UtcNow, null, null);
        State.Tasks.Add(task);
        return task;
    }

    public async Task<TaskItem?> UpdateTaskAsync(int userId, string taskId, string status, double progress, string? error)
    {
        TaskItem? task = null;
        await SaveAsync(() => task = ApplyTaskUpdate(userId, taskId, status, progress, error));
        return task;
    }

    TaskItem? ApplyTaskUpdate(int userId, string taskId, string status, double progress, string? error, Dictionary<string, object?>? result = null)
    {
        var task = State.Tasks.FirstOrDefault(x => x.Id == taskId && x.UserId == userId);
        if (task is null) return null;
        State.Tasks.Remove(task);
        var fresh = task with
        {
            Status = status,
            Progress = progress,
            ErrorMessage = error,
            Result = result ?? task.Result,
            FinishedAt = status is "completed" or "failed" or "cancelled" ? DateTimeOffset.UtcNow : null
        };
        State.Tasks.Add(fresh);
        SyncJobWithTask(fresh);
        return fresh;
    }

    static string JobStatusForTask(string taskStatus) => taskStatus switch
    {
        "completed" => "succeeded",
        "failed" => "failed",
        "cancelled" => "cancelled",
        "running" => "running",
        _ => "queued"
    };

    // Tasks are tracked as platform jobs; keep the job record in step so it never lingers as "queued".
    void SyncJobWithTask(TaskItem task)
    {
        var job = State.Jobs.FirstOrDefault(x => x.Id == task.JobId);
        if (job is null) return;
        var status = JobStatusForTask(task.Status);
        if (job.Status == status) return;
        State.Jobs.Remove(job);
        State.Jobs.Add(job with
        {
            Status = status,
            Error = task.ErrorMessage,
            StartedAt = job.StartedAt ?? task.StartedAt,
            FinishedAt = status is "succeeded" or "failed" or "cancelled" ? task.FinishedAt ?? DateTimeOffset.UtcNow : null
        });
    }

    void SyncTaskJobStatuses()
    {
        foreach (var task in State.Tasks.ToList())
        {
            SyncJobWithTask(task);
        }
    }

    Job CreateJobCore(string type, int userId, string? projectId, Dictionary<string, object?> input, Dictionary<string, object?> parameters)
    {
        var job = new Job("job_" + Util.Id(), "maskflow", type, userId, projectId, "platform", "free", "queued",
            new Dictionary<string, object?> { ["gpu"] = 1, ["timeoutSec"] = 600 }, input, null, parameters, null, 10, null, null, DateTimeOffset.UtcNow, null, null);
        State.Jobs.Add(job);
        return job;
    }

    string CreateSession(int userId, string? deviceId = null)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        State.Sessions.Add(new Session(token, userId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), deviceId));
        return token;
    }

    void InvalidateSessions(int userId, string? keepSessionToken = null)
    {
        State.Sessions.RemoveAll(x => x.UserId == userId && x.Token != keepSessionToken);
    }

    void InvalidateSessionsForDevice(int userId, string deviceId)
    {
        State.Sessions.RemoveAll(x => x.UserId == userId && x.DeviceId == deviceId);
    }

    void PruneExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        State.Sessions.RemoveAll(x => x.ExpiresAt < now);
    }

    public static string? ExtractBearerToken(HttpContext context)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = auth["Bearer ".Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    void Seed()
    {
        if (State.Pools.Count == 0)
        {
            State.Pools.Add(new Pool("platform-gpu", "Platform GPU Pool", "platform-gpu", "cn-east", "active", new() { ["gpu"] = 8, ["vramGb"] = 192 }, new() { ["priority"] = "balanced" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            State.Pools.Add(new Pool("cpu-general", "General CPU Pool", "cpu-general", "cn-east", "active", new() { ["cpu"] = 64, ["memoryGb"] = 256 }, new() { ["priority"] = "low" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        if (State.PricingRules.Count == 0)
        {
            State.PricingRules.Add(new PricingRule("price_gpu_default", "Default GPU Pricing", "gpu", "platform-gpu", "cn-east", 2.8, "hour", "active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    static readonly string DummyPasswordSalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    static readonly string DummyPasswordHash = HashPassword("maskflow-timing-equalizer", DummyPasswordSalt);

    static string HashPassword(string password, string salt)
    {
        var bytes = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(salt), 150_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static bool VerifyPassword(string password, string hash, string salt) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashPassword(password, salt)), Encoding.ASCII.GetBytes(hash));

    void ReplaceUser(User user)
    {
        State.Users.RemoveAll(x => x.Id == user.Id);
        State.Users.Add(user);
    }

    static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

    static string FormatYolo(double value) => Clamp01(value).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    // Zip entry names must be safe on every OS that may extract the dataset, and never traverse ("..").
    public static string SanitizeFileName(string value)
    {
        const string invalid = "\\/:*?\"<>|";
        var cleaned = new string(value.Select(ch => char.IsControl(ch) || invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned is "" or "." or ".." ? "_" : cleaned;
    }

}
