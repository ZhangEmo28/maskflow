using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

public static class Util
{
    public static string Id() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed partial class MaskFlowStore
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly IMaskFlowRepository repository;
    public string StorageRoot { get; }
    string MinioEndpoint => Environment.GetEnvironmentVariable("MASKFLOW_MINIO_ENDPOINT") ?? "";
    string MinioAccessKey => Environment.GetEnvironmentVariable("MASKFLOW_MINIO_ACCESS_KEY") ?? "";
    string MinioSecretKey => Environment.GetEnvironmentVariable("MASKFLOW_MINIO_SECRET_KEY") ?? "";
    string MinioBucket => Environment.GetEnvironmentVariable("MASKFLOW_MINIO_BUCKET") ?? "maskflow";
    bool UseMinio => !string.IsNullOrWhiteSpace(MinioEndpoint);
    // Readers always see the last committed snapshot; a writer sees its private working copy
    // (flowed through AsyncLocal) until the database commit succeeds and it is published.
    MaskFlowState published = new();
    readonly AsyncLocal<MaskFlowState?> writerState = new();
    public MaskFlowState State => writerState.Value ?? Volatile.Read(ref published);
    readonly List<string> deleteAfterCommit = [];
    readonly List<string> deleteOnRollback = [];
    readonly Lazy<AmazonS3Client> s3Client;
    readonly ConcurrentDictionary<string, PasswordResetEntry> passwordResetTokens = new(StringComparer.OrdinalIgnoreCase);

    sealed record PasswordResetEntry(string TokenHash, DateTimeOffset ExpiresAt);

    // storageRoot overrides MASKFLOW_STORAGE (tests use one directory per store).
    public MaskFlowStore(IMaskFlowRepository repository, string? storageRoot = null)
    {
        this.repository = repository;
        StorageRoot = storageRoot
            ?? Environment.GetEnvironmentVariable("MASKFLOW_STORAGE")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "storage");
        s3Client = new Lazy<AmazonS3Client>(CreateS3Client, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public void Initialize()
    {
        Directory.CreateDirectory(StorageRoot);
        repository.EnsureSchemaAsync().GetAwaiter().GetResult();
        var loaded = repository.LoadAsync().GetAwaiter().GetResult();
        EnsureMinioBucketAsync().GetAwaiter().GetResult();
        var state = StateDiff.Snapshot(loaded.State ?? new MaskFlowState());
        Seed(state);
        SyncTaskJobStatuses(state);
        HashLegacySessionTokens(state);
        RequeueInterruptedBatchTasks(state);
        repository.SaveChangesAsync(loaded.RequiresFullWrite ? null : loaded.State, state).GetAwaiter().GetResult();
        Volatile.Write(ref published, state);
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
        var original = Volatile.Read(ref published);
        var working = StateDiff.Snapshot(original);
        try
        {
            writerState.Value = working;
            if (mutateAsync is not null)
            {
                await mutateAsync();
            }

            await repository.SaveChangesAsync(original, working);
            Volatile.Write(ref published, working);
            storageCleanup = [.. deleteAfterCommit];
        }
        catch (Exception ex)
        {
            storageCleanup = [.. deleteOnRollback];
            failure = ex;
        }
        finally
        {
            writerState.Value = null;
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

        var tokenHash = Util.Sha256(token);
        var session = State.Sessions.FirstOrDefault(x => x.Token == tokenHash);
        if (session is not null && session.ExpiresAt >= DateTimeOffset.UtcNow)
        {
            return GetUser(session.UserId);
        }

        if (token.StartsWith("mfo_", StringComparison.Ordinal))
        {
            return UserForOAuthAccessToken(tokenHash);
        }

        if (token.StartsWith("mf_", StringComparison.Ordinal))
        {
            var apiToken = State.ApiTokens.FirstOrDefault(x => x.TokenHash == tokenHash && x.RevokedAt is null);
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
        string phash;
        await using (var hashSource = upload.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await hashSource.CopyToAsync(buffer);
            phash = ImageTools.PerceptualHashHex(buffer.ToArray());
        }

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
            item = new FileItem(fileId, currentUser.Id, projectId, safeName, path, upload.Length, "image", upload.ContentType, DateTimeOffset.UtcNow, $"/api/files/{fileId}/download", phash);
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

    async Task<string> PersistBytesAsync(byte[] bytes, int userId, string projectId, string safeName, string contentType)
    {
        var objectKey = $"users/{userId}/projects/{projectId}/uploads/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Util.Id()}_{safeName}";
        if (UseMinio)
        {
            await s3Client.Value.PutObjectAsync(new PutObjectRequest
            {
                BucketName = MinioBucket,
                Key = objectKey,
                InputStream = new MemoryStream(bytes),
                ContentType = contentType
            });
            return $"minio://{MinioBucket}/{objectKey}";
        }

        var userDir = Path.Combine(StorageRoot, userId.ToString(), "projects", projectId);
        Directory.CreateDirectory(userDir);
        var path = Path.Combine(userDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Util.Id()}_{safeName}");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    // ---- Dataset import (YOLO / COCO / LabelMe / VOC, zip or single annotation file) ----

    const int MaxImportEntries = 100_000;
    const long MaxImportBytes = 8L * 1024 * 1024 * 1024;
    const long MaxAnnotationFileBytes = 64L * 1024 * 1024;

    static async Task<byte[]> ReadBoundedAsync(Stream source, long limit, string name)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > limit) throw new BadHttpRequestException($"{name} 超过大小限制。", 413);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        _ => "image/jpeg"
    };

    public async Task<ImportSummary> ImportDatasetAsync(User user, string projectId, Stream upload, string uploadName, ImportOptions options)
    {
        if (!State.Projects.Any(x => x.Id == projectId && x.UserId == user.Id)) throw new BadHttpRequestException("Project not found.", 404);
        var onExisting = options.OnExisting == "replace" ? "replace" : "skip";
        var warnings = new List<string>();
        var annotationFiles = new List<(string Path, byte[] Bytes)>();
        var persisted = new List<(string Key, string Name, string Path, long Size, string Phash)>();
        try
        {
            if (uploadName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = new ZipArchive(upload, ZipArchiveMode.Read);
                if (zip.Entries.Count > MaxImportEntries) throw new BadHttpRequestException($"压缩包文件数超过 {MaxImportEntries} 个。", 413);
                var entries = zip.Entries
                    .Where(x => x.Name.Length > 0 && !x.FullName.StartsWith("__MACOSX/") && !x.Name.StartsWith('.'))
                    .ToList();
                foreach (var entry in entries.Where(x => DatasetImportParser.AnnotationExtensions.Contains(Path.GetExtension(x.Name).ToLowerInvariant())))
                {
                    await using var stream = entry.Open();
                    annotationFiles.Add((entry.FullName, await ReadBoundedAsync(stream, MaxAnnotationFileBytes, entry.FullName)));
                }

                if (options.ImportImages)
                {
                    var existingKeys = State.Files
                        .Where(x => x.UserId == user.Id && x.ProjectId == projectId)
                        .Select(x => DatasetImportParser.ImageKey(x.Name))
                        .ToHashSet();
                    var seen = new HashSet<string>();
                    long total = 0;
                    // One image in memory at a time: archives can be several GB.
                    foreach (var entry in entries.Where(x => UploadValidator.ImageExtensions.Contains(Path.GetExtension(x.Name).ToLowerInvariant())))
                    {
                        var key = DatasetImportParser.ImageKey(entry.FullName);
                        if (existingKeys.Contains(key)) continue;
                        if (!seen.Add(key))
                        {
                            warnings.Add($"重复的图片文件名，已忽略：{entry.FullName}");
                            continue;
                        }

                        byte[] bytes;
                        await using (var stream = entry.Open())
                        {
                            bytes = await ReadBoundedAsync(stream, UploadValidator.MaxBytes, entry.FullName);
                        }

                        total += bytes.Length;
                        if (total > MaxImportBytes) throw new BadHttpRequestException("压缩包解压后过大。", 413);
                        if (!UploadValidator.LooksLikeImage(entry.Name, bytes))
                        {
                            warnings.Add($"不是有效图片，已跳过：{entry.FullName}");
                            continue;
                        }

                        var safeName = Path.GetFileName(entry.Name);
                        var path = await PersistBytesAsync(bytes, user.Id, projectId, safeName, ContentTypeFor(safeName));
                        persisted.Add((key, safeName, path, bytes.Length, ImageTools.PerceptualHashHex(bytes)));
                    }
                }
            }
            else
            {
                annotationFiles.Add((uploadName, await ReadBoundedAsync(upload, MaxAnnotationFileBytes * 4, uploadName)));
            }

            if (annotationFiles.Count == 0 && persisted.Count == 0)
            {
                throw new BadHttpRequestException("没有找到可导入的图片或标注文件。", 400);
            }

            var parsed = annotationFiles.Count > 0
                ? DatasetImportParser.Parse(annotationFiles, options.Format)
                : new ImportParseResult("images", [], [], []);
            warnings.AddRange(parsed.Warnings);
            return await CommitImportAsync(user.Id, projectId, parsed, persisted, options, onExisting, warnings);
        }
        catch
        {
            await DeleteStoredObjectsQuietlyAsync(persisted.Select(x => x.Path));
            throw;
        }
    }

    async Task<ImportSummary> CommitImportAsync(int userId, string projectId, ImportParseResult parsed,
        List<(string Key, string Name, string Path, long Size, string Phash)> persisted, ImportOptions options, string onExisting, List<string> warnings)
    {
        ImportSummary? summary = null;
        await SaveAsync(() =>
        {
            deleteOnRollback.AddRange(persisted.Select(x => x.Path));
            var currentUser = GetUser(userId)!;
            var addedBytes = persisted.Sum(x => x.Size);
            EnsureStorageAvailable(currentUser, addedBytes);

            var labels = GetProjectLabels(userId, projectId);
            var newLabels = parsed.ClassNames.Where(x => x.Length > 0 && !labels.Contains(x, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            labels = ApplyProjectLabels(userId, projectId, labels.Concat(newLabels));

            var now = DateTimeOffset.UtcNow;
            foreach (var image in persisted)
            {
                var fileId = State.NextFileId++;
                State.Files.Add(new FileItem(fileId, userId, projectId, image.Name, image.Path, image.Size, "image", ContentTypeFor(image.Name), now, $"/api/files/{fileId}/download", image.Phash));
            }

            var filesByKey = State.Files
                .Where(x => x.UserId == userId && x.ProjectId == projectId)
                .GroupBy(x => DatasetImportParser.ImageKey(x.Name))
                .ToDictionary(x => x.Key, x => x.OrderBy(file => file.Id).First());
            int annotated = 0, imported = 0, skipped = 0;
            var unmatched = new List<string>();
            foreach (var image in parsed.Images.Values)
            {
                if (!filesByKey.TryGetValue(image.Key, out var file))
                {
                    unmatched.Add(image.Key);
                    continue;
                }

                if (onExisting == "skip" && GetAnnotationSet(userId, file.Id) is not null)
                {
                    skipped++;
                    continue;
                }

                var items = image.Shapes.Select(shape => new AnnotationItem("ann_" + Util.Id(), -1, shape.Label, shape.Box, shape.Segment, 1.0, options.Confirmed)).ToList();
                var set = ApplyAnnotationSet(userId, new AnnotationSaveRequest(file.Id, image.Width, image.Height, items));
                annotated++;
                imported += set.Annotations.Count;
            }

            ReplaceUser(currentUser with { UsedBytes = currentUser.UsedBytes + addedBytes });
            if (unmatched.Count > 0) warnings.Add($"{unmatched.Count} 个标注没有找到同名图片（例如 {string.Join("、", unmatched.Take(5))}），请一起打包图片或先上传图片。");
            summary = new ImportSummary(parsed.Format, persisted.Count, annotated, imported, skipped, newLabels, unmatched.Take(50).ToList(), warnings.Take(50).ToList());
        });
        return summary!;
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

    // Object copies can take minutes for big projects, so they run outside the write lock;
    // only the final record creation is serialized, and copies are removed if it fails.
    public async Task<Project?> CopyProjectAsync(int userId, string sourceProjectId, ProjectCopyRequest request)
    {
        var snapshot = State;
        var source = snapshot.Projects.FirstOrDefault(x => x.Id == sourceProjectId && x.UserId == userId);
        if (source is null) return null;

        var sourceFiles = snapshot.Files
            .Where(x => x.UserId == userId && x.ProjectId == sourceProjectId)
            .OrderBy(x => x.CreatedAt)
            .ToList();
        var copiedBytes = sourceFiles.Sum(x => x.Size);
        EnsureStorageAvailable(snapshot.Users.First(x => x.Id == userId), copiedBytes);

        var newProjectId = "proj_" + Util.Id();
        var copiedPaths = new Dictionary<int, string>();
        try
        {
            foreach (var sourceFile in sourceFiles)
            {
                copiedPaths[sourceFile.Id] = await CopyStoredObjectAsync(sourceFile.Path, userId, newProjectId, sourceFile.Name, sourceFile.ContentType);
            }
        }
        catch
        {
            await DeleteStoredObjectsQuietlyAsync(copiedPaths.Values);
            throw;
        }

        Project? copied = null;
        await SaveAsync(() =>
        {
            deleteOnRollback.AddRange(copiedPaths.Values);
            var current = State.Projects.FirstOrDefault(x => x.Id == sourceProjectId && x.UserId == userId)
                ?? throw new BadHttpRequestException("Project not found.", 404);
            var currentUser = GetUser(userId)!;
            EnsureStorageAvailable(currentUser, copiedBytes);

            var now = DateTimeOffset.UtcNow;
            var nextName = string.IsNullOrWhiteSpace(request.Name) ? $"{current.Name} 副本" : request.Name.Trim();
            copied = new Project(newProjectId, userId, nextName, current.Description, current.DataType, current.Split, 0, 0, now, now);
            State.Projects.Add(copied);
            State.ProjectLabels[copied.Id] = GetProjectLabels(userId, sourceProjectId).ToList();
            if (State.ProjectLabelPrompts.TryGetValue(sourceProjectId, out var prompts))
            {
                State.ProjectLabelPrompts[copied.Id] = new Dictionary<string, string>(prompts, StringComparer.OrdinalIgnoreCase);
            }

            var fileIdMap = new Dictionary<int, int>();
            foreach (var sourceFile in sourceFiles)
            {
                var fileId = State.NextFileId++;
                State.Files.Add(sourceFile with
                {
                    Id = fileId,
                    ProjectId = copied.Id,
                    Path = copiedPaths[sourceFile.Id],
                    DownloadUrl = $"/api/files/{fileId}/download"
                });
                fileIdMap[sourceFile.Id] = fileId;
            }

            foreach (var sourceSet in State.AnnotationSets.Where(x => x.UserId == userId && fileIdMap.ContainsKey(x.FileId)).ToList())
            {
                var annotations = sourceSet.Annotations.Select(x => x with { Segment = x.Segment?.ToList() }).ToList();
                State.AnnotationSets.Add(new AnnotationSet(
                    "annset_" + Util.Id(),
                    userId,
                    fileIdMap[sourceSet.FileId],
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

    static void EnsureStorageAvailable(User user, long additionalBytes)
    {
        if (user.UsedBytes + additionalBytes > user.QuotaBytes)
        {
            throw new BadHttpRequestException("Storage quota exceeded.", 403);
        }
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

            if (State.TrainingRuns.Any(x => x.ProjectId == projectId && IsActiveTraining(x)))
            {
                throw new BadHttpRequestException("项目还有训练在进行，请先取消训练再删除项目。", 409);
            }

            var fileIds = State.Files.Where(x => x.UserId == userId && x.ProjectId == projectId).Select(x => x.Id).ToList();
            foreach (var fileId in fileIds)
            {
                DeleteFileCoreLocked(userId, fileId);
            }

            foreach (var run in State.TrainingRuns.Where(x => x.ProjectId == projectId).ToList())
            {
                RemoveTrainingRunLocked(run);
            }

            State.Tasks.RemoveAll(x => x.UserId == userId && x.ProjectId == projectId);
            foreach (var export in State.Exports.Where(x => x.UserId == userId && x.ProjectId == projectId && x.Path is not null))
            {
                deleteAfterCommit.Add(export.Path!);
            }

            State.Exports.RemoveAll(x => x.UserId == userId && x.ProjectId == projectId);
            State.ProjectLabels.Remove(projectId);
            State.ProjectLabelPrompts.Remove(projectId);
            State.ProjectMembers.RemoveAll(x => x.ProjectId == projectId);
            var videoBytes = 0L;
            foreach (var video in State.Videos.Where(x => x.ProjectId == projectId))
            {
                deleteAfterCommit.Add(video.Path);
                videoBytes += video.Size;
            }

            if (videoBytes > 0 && GetUser(userId) is { } owner)
            {
                ReplaceUser(owner with { UsedBytes = Math.Max(0, owner.UsedBytes - videoBytes) });
            }

            State.Videos.RemoveAll(x => x.ProjectId == projectId);
            State.ProjectInvites.RemoveAll(x => x.ProjectId == projectId);
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
        State.FileAssignments.RemoveAll(x => x.FileId == fileId);
        deleteAfterCommit.Add(file.Path);

        var user = GetUser(userId);
        if (user is not null)
        {
            ReplaceUser(user with { UsedBytes = Math.Max(0, user.UsedBytes - file.Size) });
        }

        return true;
    }

    // userId is the data owner. Annotators never confirm their own work: their saves clear
    // confirmations and send the image back to "draft" until a reviewer approves it.
    public async Task<AnnotationSet> SaveAnnotationSetAsync(int userId, AnnotationSaveRequest request, string role = "owner")
    {
        AnnotationSet? set = null;
        await SaveAsync(() => set = role == "annotator"
            ? ApplyAnnotationSet(userId, request with { Annotations = (request.Annotations ?? []).Select(x => x with { Confirmed = false }).ToList() }, "draft")
            : ApplyAnnotationSet(userId, request));
        return set!;
    }

    AnnotationSet ApplyAnnotationSet(int userId, AnnotationSaveRequest request, string? reviewStatus = null)
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
            now,
            reviewStatus ?? existing?.ReviewStatus ?? "draft",
            existing?.ReviewComment,
            existing?.ReviewedBy,
            existing?.ReviewedAt,
            existing?.SubmittedBy,
            annotations.Count == 0 && request.Background);
        State.AnnotationSets.Add(set);
        return set;
    }

    public async Task<(AnnotationSet Annotation, TaskItem? Task)> PersistAutoAnnotationAsync(int userId, AnnotationSaveRequest request, FileItem file, int quotaAmount = 1, bool recordTask = true)
    {
        AnnotationSet? set = null;
        TaskItem? completed = null;
        await SaveAsync(() =>
        {
            if (!State.Files.Any(x => x.Id == file.Id && x.UserId == userId))
            {
                throw new BadHttpRequestException("File not found.", 404);
            }

            var existing = GetAnnotationSet(userId, file.Id);
            var merged = AnnotationMerge.KeepConfirmed(existing?.Annotations, request.Annotations);
            set = ApplyAnnotationSet(userId, request with { Annotations = merged });
            ApplyQuotaConsumption(userId, quotaAmount);
            if (!recordTask) return;
            var task = CreateTaskCore(userId, "yolo_auto_annotation", $"自动标注 {file.Name}", file.ProjectId, file.Id, 1);
            completed = ApplyTaskUpdate(userId, task.Id, "completed", 1, null, new Dictionary<string, object?>
            {
                ["fileId"] = file.Id,
                ["annotationCount"] = set.Annotations.Count,
                ["keptConfirmed"] = merged.Count(x => x.Confirmed)
            });
        });
        return (set!, completed);
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
        PruneLabelPrompts(projectId, cleaned);
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

    // An image is annotated once it has a target, or a person marked it as background (no targets).
    // An empty set otherwise means nothing was found yet, e.g. AI runs that detected nothing in older versions.
    public static bool IsAnnotated(AnnotationSet? set) => set is not null && (set.Annotations.Count > 0 || set.Background);

    // unannotated | background | pending (some targets await human confirmation) | confirmed
    public static string AnnotationStatus(AnnotationSet? set) =>
        !IsAnnotated(set) ? "unannotated"
        : set!.Annotations.Count == 0 ? "background"
        : set.Annotations.Any(x => !x.Confirmed) ? "pending"
        : "confirmed";

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

    // Images uploaded before perceptual hashing existed get their hash on first analysis/export.
    public async Task EnsurePerceptualHashesAsync(int userId, string? projectId, CancellationToken cancellationToken = default)
    {
        var missing = State.Files
            .Where(x => x.UserId == userId && x.Kind == "image" && x.Phash is null && (projectId is null || x.ProjectId == projectId))
            .ToList();
        if (missing.Count == 0) return;

        var hashes = new Dictionary<int, string>();
        foreach (var file in missing)
        {
            try
            {
                hashes[file.Id] = ImageTools.PerceptualHashHex(await ReadStoredBytesAsync(file.Path, cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmazonS3Exception)
            {
                hashes[file.Id] = "";
            }
        }

        await SaveAsync(() =>
        {
            for (var i = 0; i < State.Files.Count; i++)
            {
                var file = State.Files[i];
                if (file.Phash is null && hashes.TryGetValue(file.Id, out var hash))
                {
                    State.Files[i] = file with { Phash = hash };
                }
            }
        });
    }

    async Task<byte[]> ReadStoredBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenStoredObjectAsync(path);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    // The archive is built from the committed snapshot without holding the write lock;
    // only registering the finished export is serialized.
    public async Task<DatasetExport> CreateDatasetExportAsync(int userId, ExportRequest request)
    {
        var split = request.Split ?? new SplitConfig(70, 20, 10);
        DatasetSplitter.Validate(split);
        if (request.GroupNearDuplicates) await EnsurePerceptualHashesAsync(userId, request.ProjectId);
        var plan = DatasetExportPlan.Create(State, userId, request);
        plan.ValidateExport();

        var exportId = "export_" + Util.Id();
        var exportDir = Path.Combine(StorageRoot, userId.ToString(), "exports");
        Directory.CreateDirectory(exportDir);
        var zipPath = Path.Combine(exportDir, $"{exportId}.zip");
        try
        {
            await WriteDatasetArchiveAsync(zipPath, plan, split, request.Seed);
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
                File.Delete(zipPath);
            }

            var export = new DatasetExport(exportId, userId, request.ProjectId, request.TaskId, "completed", exportPath, zipSize, request with { Format = plan.Format, Split = split }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, $"/api/export/{exportId}/download");
            await SaveAsync(() =>
            {
                deleteOnRollback.Add(exportPath);
                State.Exports.Add(export);
            });
            return export;
        }
        catch
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            throw;
        }
    }

    async Task WriteDatasetArchiveAsync(string zipPath, DatasetExportPlan plan, SplitConfig split, int seed)
    {
        var annotationMap = plan.Annotations;
        var labeledFiles = plan.Files;
        var exportFormat = plan.Format;
        var exportDataType = plan.DataType;
        var yoloTask = ResolveYoloTask(exportDataType);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var labels = plan.Labels;
            var samples = plan.Samples;
            var assignments = DatasetSplitter.Assign(samples, split, seed, plan.GroupOf);

            if (exportFormat == "classification-crops")
            {
                await WriteClassificationCropDatasetAsync(archive, labeledFiles, annotationMap, labels, assignments);
            }
            else if (exportFormat == "coco")
            {
                await WriteCocoDatasetAsync(archive, plan, assignments);
            }
            else if (exportFormat == "voc")
            {
                await WriteVocDatasetAsync(archive, plan, assignments);
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
                    DatasetSplitter.Report(samples, labels, assignments, split, seed, plan.GroupOf),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

            var readme = archive.CreateEntry("README.md");
            using var readmeWriter = new StreamWriter(readme.Open());
            readmeWriter.WriteLine(exportFormat switch
            {
                "classification-crops" => "# MaskFlow Classification Crop Dataset",
                "coco" => "# MaskFlow COCO Dataset",
                "voc" => "# MaskFlow Pascal VOC Dataset",
                _ => "# MaskFlow YOLO Dataset"
            });
            readmeWriter.WriteLine();
            readmeWriter.WriteLine($"Format: {exportFormat}");
            readmeWriter.WriteLine($"Task: {(exportFormat == "classification-crops" ? "classify" : plan.IsYolo ? yoloTask : exportDataType)}");
            if (plan.ConfirmedOnly) readmeWriter.WriteLine("Annotations: human-confirmed targets only");
            readmeWriter.WriteLine($"DataType: {exportDataType}");
            readmeWriter.WriteLine($"Images: {labeledFiles.Count}");
            readmeWriter.WriteLine($"Split: stratified by label, grouped by source image and near-duplicate group; seed={seed}");
            readmeWriter.WriteLine("See split-report.json for actual per-class image/annotation counts, file assignments and coverage warnings.");
            readmeWriter.WriteLine($"GeneratedAt: {DateTimeOffset.UtcNow:O}");
        }

    }

    static string ExportStem(FileItem file) => SanitizeFileName($"{file.Id}_{Path.GetFileNameWithoutExtension(file.Name)}");

    static (int Width, int Height) ExportSize(AnnotationSet set, byte[] bytes) =>
        set.Width > 0 && set.Height > 0 ? (set.Width, set.Height) : ImageTools.OrientedSize(bytes);

    // COCO: images/{split}/..., annotations/instances_{split}.json; category ids are 1-based.
    async Task WriteCocoDatasetAsync(ZipArchive archive, DatasetExportPlan plan, IReadOnlyDictionary<int, string> assignments)
    {
        var categories = plan.Labels.Select((name, index) => new { id = index + 1, name, supercategory = "object" }).ToArray();
        var images = DatasetSplitter.Names.ToDictionary(name => name, _ => new List<object>());
        var annotations = DatasetSplitter.Names.ToDictionary(name => name, _ => new List<object>());
        var annotationId = 1;
        foreach (var file in plan.Files)
        {
            var split = assignments[file.Id];
            var bytes = await ReadStoredBytesAsync(file.Path);
            var set = plan.Annotations[file.Id];
            var (width, height) = ExportSize(set, bytes);
            var fileName = ExportStem(file) + Path.GetExtension(file.Name);
            await using (var entry = archive.CreateEntry($"images/{split}/{fileName}").Open())
            {
                await entry.WriteAsync(bytes);
            }

            images[split].Add(new { id = file.Id, file_name = fileName, width, height });
            foreach (var item in set.Annotations.Where(IsExportableAnnotation))
            {
                var left = Math.Round(Clamp01(item.Bbox.Cx - item.Bbox.Width / 2) * width, 2);
                var top = Math.Round(Clamp01(item.Bbox.Cy - item.Bbox.Height / 2) * height, 2);
                var boxWidth = Math.Round(Clamp01(item.Bbox.Width) * width, 2);
                var boxHeight = Math.Round(Clamp01(item.Bbox.Height) * height, 2);
                var hasPolygon = HasValidSegment(item);
                double[] polygon = hasPolygon
                    ? item.Segment!.Select((value, index) => Math.Round(value * (index % 2 == 0 ? width : height), 2)).ToArray()
                    : [];
                annotations[split].Add(new
                {
                    id = annotationId++,
                    image_id = file.Id,
                    category_id = item.ClassId + 1,
                    bbox = new[] { left, top, boxWidth, boxHeight },
                    area = Math.Round(hasPolygon ? AutoAnnotationParser.PolygonArea(polygon) : boxWidth * boxHeight, 2),
                    segmentation = hasPolygon ? new[] { polygon } : [],
                    iscrowd = 0
                });
            }
        }

        foreach (var split in DatasetSplitter.Names)
        {
            await using var stream = archive.CreateEntry($"annotations/instances_{split}.json").Open();
            await JsonSerializer.SerializeAsync(stream, new
            {
                info = new { description = "Exported by MaskFlow", date_created = DateTimeOffset.UtcNow.ToString("O") },
                licenses = Array.Empty<object>(),
                images = images[split],
                annotations = annotations[split],
                categories
            });
        }
    }

    // Pascal VOC: JPEGImages/, Annotations/*.xml (1-based pixel boxes), ImageSets/Main/{split}.txt.
    async Task WriteVocDatasetAsync(ZipArchive archive, DatasetExportPlan plan, IReadOnlyDictionary<int, string> assignments)
    {
        var splitStems = DatasetSplitter.Names.ToDictionary(name => name, _ => new List<string>());
        foreach (var file in plan.Files)
        {
            var bytes = await ReadStoredBytesAsync(file.Path);
            var set = plan.Annotations[file.Id];
            var (width, height) = ExportSize(set, bytes);
            var stem = ExportStem(file);
            var fileName = stem + Path.GetExtension(file.Name);
            await using (var entry = archive.CreateEntry($"JPEGImages/{fileName}").Open())
            {
                await entry.WriteAsync(bytes);
            }

            var objects = set.Annotations.Where(IsExportableAnnotation).Select(item =>
            {
                var xmin = Math.Clamp((int)Math.Floor(Clamp01(item.Bbox.Cx - item.Bbox.Width / 2) * width) + 1, 1, width);
                var ymin = Math.Clamp((int)Math.Floor(Clamp01(item.Bbox.Cy - item.Bbox.Height / 2) * height) + 1, 1, height);
                var xmax = Math.Clamp((int)Math.Ceiling(Clamp01(item.Bbox.Cx + item.Bbox.Width / 2) * width), xmin, width);
                var ymax = Math.Clamp((int)Math.Ceiling(Clamp01(item.Bbox.Cy + item.Bbox.Height / 2) * height), ymin, height);
                return new XElement("object",
                    new XElement("name", item.Label),
                    new XElement("pose", "Unspecified"),
                    new XElement("truncated", 0),
                    new XElement("difficult", 0),
                    new XElement("bndbox",
                        new XElement("xmin", xmin),
                        new XElement("ymin", ymin),
                        new XElement("xmax", xmax),
                        new XElement("ymax", ymax)));
            });
            var document = new XDocument(new XElement("annotation",
                new XElement("folder", "JPEGImages"),
                new XElement("filename", fileName),
                new XElement("source", new XElement("database", "MaskFlow")),
                new XElement("size", new XElement("width", width), new XElement("height", height), new XElement("depth", 3)),
                new XElement("segmented", 0),
                objects));
            await using (var entry = archive.CreateEntry($"Annotations/{stem}.xml").Open())
            {
                await document.SaveAsync(entry, SaveOptions.None, CancellationToken.None);
            }

            splitStems[assignments[file.Id]].Add(stem);
        }

        foreach (var (split, stems) in splitStems)
        {
            await using var writer = new StreamWriter(archive.CreateEntry($"ImageSets/Main/{split}.txt").Open());
            foreach (var stem in stems) await writer.WriteLineAsync(stem);
        }

        await using var labelWriter = new StreamWriter(archive.CreateEntry("labels.txt").Open());
        foreach (var label in plan.Labels) await labelWriter.WriteLineAsync(label);
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


    // ---- SAM 3 label prompts ----

    public Dictionary<string, string> GetLabelPrompts(int userId, string projectId)
    {
        GetProjectLabels(userId, projectId);
        return State.ProjectLabelPrompts.TryGetValue(projectId, out var prompts)
            ? new Dictionary<string, string>(prompts, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public string PromptForLabel(string projectId, string label) =>
        State.ProjectLabelPrompts.TryGetValue(projectId, out var prompts) && prompts.TryGetValue(label, out var prompt) && !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : label;

    public async Task<Dictionary<string, string>> SaveLabelPromptsAsync(int userId, string projectId, IReadOnlyDictionary<string, string?>? prompts)
    {
        Dictionary<string, string>? saved = null;
        await SaveAsync(() =>
        {
            var labels = GetProjectLabels(userId, projectId);
            var requested = new Dictionary<string, string?>(prompts ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in labels)
            {
                var prompt = requested.TryGetValue(label, out var value) ? value?.Trim() : null;
                if (string.IsNullOrEmpty(prompt) || prompt.Equals(label, StringComparison.Ordinal)) continue;
                if (prompt.Length > 100) throw new BadHttpRequestException($"提示词过长：{label}（最多 100 个字符）。", 400);
                next[label] = prompt;
            }

            // Replace (never mutate) the inner dictionary so the snapshot diff sees the change.
            if (next.Count == 0) State.ProjectLabelPrompts.Remove(projectId);
            else State.ProjectLabelPrompts[projectId] = next;
            saved = next;
        });
        return saved!;
    }

    void PruneLabelPrompts(string projectId, IReadOnlyCollection<string> labels)
    {
        if (!State.ProjectLabelPrompts.TryGetValue(projectId, out var prompts)) return;
        var kept = prompts.Where(x => labels.Contains(x.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        if (kept.Count == prompts.Count) return;
        if (kept.Count == 0) State.ProjectLabelPrompts.Remove(projectId);
        else State.ProjectLabelPrompts[projectId] = kept;
    }

    // ---- Custom ONNX models ----

    static long MaxModelBytes =>
        long.TryParse(Environment.GetEnvironmentVariable("MASKFLOW_MAX_MODEL_BYTES"), out var value) && value > 0 ? value : 500L * 1024 * 1024;

    public async Task<AiModel> AddAiModelAsync(User user, IFormFile upload, string? name, string? projectId)
    {
        if (!Path.GetExtension(upload.FileName).Equals(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new BadHttpRequestException("只支持 ONNX 模型（.onnx）。可用 `yolo export model=best.pt format=onnx` 导出。", 400);
        }

        if (upload.Length == 0 || upload.Length > MaxModelBytes)
        {
            throw new BadHttpRequestException($"模型文件大小需在 1 字节到 {MaxModelBytes / (1024 * 1024)} MB 之间。", 413);
        }

        projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId;
        if (projectId is not null && !State.Projects.Any(x => x.Id == projectId && x.UserId == user.Id))
        {
            throw new BadHttpRequestException("Project not found.", 404);
        }

        EnsureStorageAvailable(GetUser(user.Id)!, upload.Length);
        string sha256;
        await using (var hashStream = upload.OpenReadStream())
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream)).ToLowerInvariant();
        }

        var modelId = "model_" + Util.Id();
        var path = await PersistModelAsync(upload, user.Id, modelId);
        var displayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(upload.FileName) : name.Trim();
        var model = new AiModel(modelId, user.Id, projectId, displayName[..Math.Min(displayName.Length, 200)], path, upload.Length, sha256, null, DateTimeOffset.UtcNow);
        await SaveAsync(() =>
        {
            deleteOnRollback.Add(path);
            var currentUser = GetUser(user.Id)!;
            EnsureStorageAvailable(currentUser, upload.Length);
            State.AiModels.Add(model);
            ReplaceUser(currentUser with { UsedBytes = currentUser.UsedBytes + upload.Length });
        });
        return model;
    }

    async Task<string> PersistModelAsync(IFormFile upload, int userId, string modelId)
    {
        if (UseMinio)
        {
            var key = $"users/{userId}/models/{modelId}.onnx";
            await using var stream = upload.OpenReadStream();
            await s3Client.Value.PutObjectAsync(new PutObjectRequest
            {
                BucketName = MinioBucket,
                Key = key,
                InputStream = stream,
                ContentType = "application/octet-stream"
            });
            return $"minio://{MinioBucket}/{key}";
        }

        var directory = Path.Combine(StorageRoot, userId.ToString(), "models");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{modelId}.onnx");
        await using var target = File.Create(path);
        await upload.CopyToAsync(target);
        return path;
    }

    public async Task<bool> DeleteAiModelAsync(int userId, string modelId)
    {
        var deleted = false;
        await SaveAsync(() =>
        {
            var model = State.AiModels.FirstOrDefault(x => x.Id == modelId && x.UserId == userId);
            if (model is null) return;
            State.AiModels.Remove(model);
            deleteAfterCommit.Add(model.Path);
            var user = GetUser(userId);
            if (user is not null) ReplaceUser(user with { UsedBytes = Math.Max(0, user.UsedBytes - model.Size) });
            deleted = true;
        });
        return deleted;
    }

    public Task UpdateAiModelClassNamesAsync(string modelId, List<string> classNames) =>
        SaveAsync(() =>
        {
            var model = State.AiModels.FirstOrDefault(x => x.Id == modelId);
            if (model is null || (model.ClassNames is not null && model.ClassNames.SequenceEqual(classNames))) return;
            State.AiModels.Remove(model);
            State.AiModels.Add(model with { ClassNames = classNames });
        });

    // ---- Background batch annotation ----

    public const string BatchAnnotationTaskType = "batch_auto_annotation";

    public async Task<TaskItem> CreateBatchAnnotationTaskAsync(int userId, AnnotationBatchRequest request, IReadOnlyList<int> fileIds)
    {
        TaskItem? task = null;
        await SaveAsync(() =>
        {
            task = CreateTaskCore(userId, BatchAnnotationTaskType, $"批量自动标注 {fileIds.Count} 张图片", request.ProjectId, null, fileIds.Count,
                new Dictionary<string, object?>
                {
                    ["fileIds"] = fileIds.ToList(),
                    ["mode"] = request.Mode,
                    ["conf"] = request.Conf,
                    ["defaultLabel"] = request.DefaultLabel,
                    ["modelId"] = request.ModelId
                });
            task = ApplyTaskUpdate(userId, task.Id, "queued", 0, null, BatchProgress.Empty.ToResult(fileIds.Count))!;
        });
        return task!;
    }

    public BatchTaskInput? GetBatchInput(TaskItem task)
    {
        var job = State.Jobs.FirstOrDefault(x => x.Id == task.JobId);
        return job is null ? null : BatchTaskInput.From(job.Input);
    }

    // Moves a queued batch to running. Returns false if it was cancelled or picked up meanwhile.
    public async Task<bool> TryStartBatchTaskAsync(string taskId)
    {
        var started = false;
        await SaveAsync(() =>
        {
            var task = State.Tasks.FirstOrDefault(x => x.Id == taskId);
            if (task is null || task.Status != "queued") return;
            ApplyTaskUpdate(task.UserId, task.Id, "running", task.Progress, null);
            started = true;
        });
        return started;
    }

    // Records progress without overriding a cancellation made by the user in the meantime.
    public async Task<string?> RecordBatchProgressAsync(string taskId, BatchProgress progress, int total, string? finalStatus = null)
    {
        string? status = null;
        await SaveAsync(() =>
        {
            var task = State.Tasks.FirstOrDefault(x => x.Id == taskId);
            if (task is null) return;
            var nextStatus = task.Status == "running" && finalStatus is not null ? finalStatus : task.Status;
            var fraction = total == 0 ? 1 : Math.Round(progress.Processed / (double)total, 4);
            var error = nextStatus == "failed" ? progress.LastError : task.ErrorMessage;
            status = ApplyTaskUpdate(task.UserId, task.Id, nextStatus, fraction, error, progress.ToResult(total))!.Status;
        });
        return status;
    }

    static void RequeueInterruptedBatchTasks(MaskFlowState state)
    {
        for (var i = 0; i < state.Tasks.Count; i++)
        {
            var task = state.Tasks[i];
            if (task.Type == BatchAnnotationTaskType && task.Status == "running")
            {
                state.Tasks[i] = task with { Status = "queued" };
                SyncJobWithTask(state, state.Tasks[i]);
            }
        }
    }

    public async Task<TaskItem> CreateTaskAsync(int userId, string type, string? title, string? projectId, int? fileId, int imageCount)
    {
        TaskItem? task = null;
        await SaveAsync(() => task = CreateTaskCore(userId, type, title, projectId, fileId, imageCount));
        return task!;
    }

    TaskItem CreateTaskCore(int userId, string type, string? title, string? projectId, int? fileId, int imageCount, Dictionary<string, object?>? input = null)
    {
        var job = CreateJobCore(type.Replace("auto_", "sam."), userId, projectId, input ?? new Dictionary<string, object?>(), new Dictionary<string, object?>());
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
            StartedAt = status == "running" ? task.StartedAt ?? DateTimeOffset.UtcNow : task.StartedAt,
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
    void SyncJobWithTask(TaskItem task) => SyncJobWithTask(State, task);

    static void SyncJobWithTask(MaskFlowState state, TaskItem task)
    {
        var job = state.Jobs.FirstOrDefault(x => x.Id == task.JobId);
        if (job is null) return;
        var status = JobStatusForTask(task.Status);
        if (job.Status == status) return;
        state.Jobs.Remove(job);
        state.Jobs.Add(job with
        {
            Status = status,
            Error = task.ErrorMessage,
            StartedAt = job.StartedAt ?? task.StartedAt,
            FinishedAt = status is "succeeded" or "failed" or "cancelled" ? task.FinishedAt ?? DateTimeOffset.UtcNow : null
        });
    }

    static void SyncTaskJobStatuses(MaskFlowState state)
    {
        foreach (var task in state.Tasks.ToList())
        {
            SyncJobWithTask(state, task);
        }
    }

    Job CreateJobCore(string type, int userId, string? projectId, Dictionary<string, object?> input, Dictionary<string, object?> parameters)
    {
        var job = new Job("job_" + Util.Id(), "maskflow", type, userId, projectId, "platform", "free", "queued",
            new Dictionary<string, object?> { ["gpu"] = 1, ["timeoutSec"] = 600 }, input, null, parameters, null, 10, null, null, DateTimeOffset.UtcNow, null, null);
        State.Jobs.Add(job);
        return job;
    }

    // Only a SHA-256 of the session token is stored, so a database leak does not leak live sessions.
    string CreateSession(int userId, string? deviceId = null)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        State.Sessions.Add(new Session(Util.Sha256(token), userId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), deviceId));
        return token;
    }

    void InvalidateSessions(int userId, string? keepSessionToken = null)
    {
        var keepHash = keepSessionToken is null ? null : Util.Sha256(keepSessionToken);
        State.Sessions.RemoveAll(x => x.UserId == userId && x.Token != keepHash);
    }

    static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Sessions written before hashing was introduced stored the raw token; hash them in place.
    static void HashLegacySessionTokens(MaskFlowState state)
    {
        for (var i = 0; i < state.Sessions.Count; i++)
        {
            var session = state.Sessions[i];
            if (!IsSha256Hex(session.Token))
            {
                state.Sessions[i] = session with { Token = Util.Sha256(session.Token) };
            }
        }
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

    static void Seed(MaskFlowState state)
    {
        if (state.Pools.Count == 0)
        {
            state.Pools.Add(new Pool("platform-gpu", "Platform GPU Pool", "platform-gpu", "cn-east", "active", new() { ["gpu"] = 8, ["vramGb"] = 192 }, new() { ["priority"] = "balanced" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            state.Pools.Add(new Pool("cpu-general", "General CPU Pool", "cpu-general", "cn-east", "active", new() { ["cpu"] = 64, ["memoryGb"] = 256 }, new() { ["priority"] = "low" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        if (state.PricingRules.Count == 0)
        {
            state.PricingRules.Add(new PricingRule("price_gpu_default", "Default GPU Pricing", "gpu", "platform-gpu", "cn-east", 2.8, "hour", "active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
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
