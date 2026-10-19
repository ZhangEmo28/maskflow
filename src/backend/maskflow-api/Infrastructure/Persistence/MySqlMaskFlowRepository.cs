using System.Data;
using System.Text.Json;
using MySqlConnector;

public sealed class MySqlMaskFlowRepository : IMaskFlowRepository
{
    readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    readonly string connectionString;

    public MySqlMaskFlowRepository()
    {
        var configured = Environment.GetEnvironmentVariable("MASKFLOW_MYSQL");
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "MASKFLOW_MYSQL environment variable is required. Example: Server=127.0.0.1;Port=3306;Database=maskflow;User ID=maskflow;Password=maskflow;Allow User Variables=true;");
        }

        connectionString = configured;
    }

    public MySqlMaskFlowRepository(string connectionString)
    {
        this.connectionString = connectionString;
    }

    public async Task EnsureSchemaAsync()
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var database = string.IsNullOrWhiteSpace(builder.Database) ? "maskflow" : builder.Database;
        builder.Database = "";

        await using (var serverConnection = new MySqlConnection(builder.ConnectionString))
        {
            await serverConnection.OpenAsync();
            await ExecAsync(serverConnection, null, $"CREATE DATABASE IF NOT EXISTS `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var sql in SchemaSql)
        {
            try { await ExecAsync(connection, null, sql); }
            catch (MySqlException ex) when (ex.Message.Contains("Duplicate")) { }
        }
    }

    public async Task<MaskFlowLoadResult> LoadAsync()
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        if (!await HasAnyDataAsync(connection))
        {
            var legacy = await TryLoadLegacyStateAsync(connection);
            return new MaskFlowLoadResult(legacy, RequiresFullWrite: true);
        }

        var state = new MaskFlowState();
        await LoadCountersAsync(connection, state);
        await LoadUsersAsync(connection, state);
        await LoadSessionsAsync(connection, state);
        await LoadProjectsAsync(connection, state);
        await LoadProjectLabelsAsync(connection, state);
        await LoadLabelPromptsAsync(connection, state);
        await LoadAiModelsAsync(connection, state);
        await LoadProjectMembersAsync(connection, state);
        await LoadProjectInvitesAsync(connection, state);
        await LoadFileAssignmentsAsync(connection, state);
        await LoadVideosAsync(connection, state);
        await LoadTrainingRunsAsync(connection, state);
        await LoadFilesAsync(connection, state);
        await LoadAnnotationSetsAsync(connection, state);
        await LoadTasksAsync(connection, state);
        await LoadJobsAsync(connection, state);
        await LoadJobEventsAsync(connection, state);
        await LoadNodesAsync(connection, state);
        await LoadPoolsAsync(connection, state);
        await LoadPricingRulesAsync(connection, state);
        await LoadWalletLedgerAsync(connection, state);
        await LoadSettlementsAsync(connection, state);
        await LoadApiTokensAsync(connection, state);
        await LoadTeamMembersAsync(connection, state);
        await LoadDevicesAsync(connection, state);
        await LoadExportsAsync(connection, state);
        await LoadQuotasAsync(connection, state);
        await LoadNotificationsAsync(connection, state);
        return new MaskFlowLoadResult(state, RequiresFullWrite: false);
    }

    // Writes only what differs between the last persisted snapshot and the new one.
    // previous == null means "write everything" (first boot from a legacy JSON state).
    public async Task SaveChangesAsync(MaskFlowState? previous, MaskFlowState current)
    {
        var before = previous ?? new MaskFlowState();
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        try
        {
            if (previous is null
                || before.NextUserId != current.NextUserId
                || before.NextFileId != current.NextFileId
                || before.NextEventId != current.NextEventId)
            {
                await UpsertRowsAsync(connection, tx, "app_counters",
                    ["id", "next_user_id", "next_file_id", "next_event_id"],
                    [[1, current.NextUserId, current.NextFileId, current.NextEventId]]);
            }

            await ApplyAsync(connection, tx, "users", "id",
                ["id", "email", "password_hash", "salt", "plan", "quota_bytes", "used_bytes", "created_at", "username", "phone", "avatar_path"],
                before.Users, current.Users, x => x.Id,
                x => [x.Id, x.Email, x.PasswordHash, x.Salt, x.Plan, x.QuotaBytes, x.UsedBytes, x.CreatedAt, x.Username, x.Phone, x.AvatarPath]);

            await ApplyAsync(connection, tx, "sessions", "token",
                ["token", "user_id", "created_at", "expires_at", "device_id"],
                before.Sessions, current.Sessions, x => x.Token,
                x => [x.Token, x.UserId, x.CreatedAt, x.ExpiresAt, x.DeviceId]);

            await ApplyAsync(connection, tx, "projects", "id",
                ["id", "user_id", "name", "description", "data_type", "split_json", "image_count", "annotation_count", "created_at", "updated_at"],
                before.Projects, current.Projects, x => x.Id,
                x => [x.Id, x.UserId, x.Name, x.Description, x.DataType, Json(x.Split), x.ImageCount, x.AnnotationCount, x.CreatedAt, x.UpdatedAt]);

            await ApplyProjectLabelsAsync(connection, tx, before.ProjectLabels, current.ProjectLabels);
            await ApplyLabelPromptsAsync(connection, tx, before.ProjectLabelPrompts, current.ProjectLabelPrompts);

            await ApplyAsync(connection, tx, "ai_models", "id",
                ["id", "user_id", "project_id", "name", "object_path", "size_bytes", "sha256", "class_names_json", "created_at"],
                before.AiModels, current.AiModels, x => x.Id,
                x => [x.Id, x.UserId, x.ProjectId, x.Name, x.Path, x.Size, x.Sha256, JsonOrNull(x.ClassNames), x.CreatedAt]);

            await ApplyAsync(connection, tx, "files", "id",
                ["id", "user_id", "project_id", "name", "object_path", "size_bytes", "kind", "content_type", "created_at", "download_url", "phash"],
                before.Files, current.Files, x => x.Id,
                x => [x.Id, x.UserId, x.ProjectId, x.Name, x.Path, x.Size, x.Kind, x.ContentType, x.CreatedAt, x.DownloadUrl, x.Phash]);

            await ApplyAsync(connection, tx, "annotation_sets", "id",
                ["id", "user_id", "file_id", "width", "height", "annotations_json", "yolo_txt", "created_at", "updated_at",
                 "review_status", "review_comment", "reviewed_by", "reviewed_at", "submitted_by"],
                before.AnnotationSets, current.AnnotationSets, x => x.Id,
                x => [x.Id, x.UserId, x.FileId, x.Width, x.Height, Json(x.Annotations), x.YoloTxt, x.CreatedAt, x.UpdatedAt,
                      x.ReviewStatus, x.ReviewComment, x.ReviewedBy, x.ReviewedAt, x.SubmittedBy]);

            await ApplyAsync(connection, tx, "project_members", "id",
                ["id", "project_id", "user_id", "role", "invited_by", "created_at"],
                before.ProjectMembers, current.ProjectMembers, x => x.Id,
                x => [x.Id, x.ProjectId, x.UserId, x.Role, x.InvitedBy, x.CreatedAt]);

            await ApplyAsync(connection, tx, "project_invites", "id",
                ["id", "project_id", "email", "role", "token_hash", "invited_by", "created_at", "expires_at"],
                before.ProjectInvites, current.ProjectInvites, x => x.Id,
                x => [x.Id, x.ProjectId, x.Email, x.Role, x.TokenHash, x.InvitedBy, x.CreatedAt, x.ExpiresAt]);

            await ApplyAsync(connection, tx, "videos", "id",
                ["id", "user_id", "project_id", "name", "object_path", "size_bytes", "fps", "duration_sec", "every_seconds", "frame_file_ids_json", "frame_indexes_json", "status", "error", "created_at"],
                before.Videos, current.Videos, x => x.Id,
                x => [x.Id, x.UserId, x.ProjectId, x.Name, x.Path, x.Size, x.Fps, x.DurationSec, x.EverySeconds, Json(x.FrameFileIds), Json(x.FrameIndexes), x.Status, x.Error, x.CreatedAt]);

            await ApplyAsync(connection, tx, "training_runs", "id",
                ["id", "user_id", "project_id", "created_by", "name", "task", "format", "request_json", "status", "epoch", "history_json", "metrics_json",
                 "artifacts_json", "model_id", "image_count", "error", "created_at", "started_at", "finished_at"],
                before.TrainingRuns, current.TrainingRuns, x => x.Id,
                x => [x.Id, x.UserId, x.ProjectId, x.CreatedBy, x.Name, x.Task, x.Format, Json(x.Request), x.Status, x.Epoch, Json(x.History),
                      x.Metrics is null ? null : x.Metrics.Value.GetRawText(), Json(x.Artifacts), x.ModelId, x.ImageCount, x.Error, x.CreatedAt, x.StartedAt, x.FinishedAt]);

            await ApplyAsync(connection, tx, "file_assignments", "file_id",
                ["file_id", "user_id", "assigned_by", "assigned_at"],
                before.FileAssignments, current.FileAssignments, x => x.FileId,
                x => [x.FileId, x.UserId, x.AssignedBy, x.AssignedAt]);

            await ApplyAsync(connection, tx, "tasks", "id",
                ["id", "user_id", "job_id", "type", "title", "project_id", "file_id", "image_count", "status", "progress", "result_json", "error_message", "created_at", "started_at", "finished_at"],
                before.Tasks, current.Tasks, x => x.Id,
                x => [x.Id, x.UserId, x.JobId, x.Type, x.Title, x.ProjectId, x.FileId, x.ImageCount, x.Status, x.Progress, JsonOrNull(x.Result), x.ErrorMessage, x.CreatedAt, x.StartedAt, x.FinishedAt]);

            await ApplyAsync(connection, tx, "jobs", "id",
                ["id", "app", "type", "user_id", "project_id", "pool", "priority", "status", "resources_json", "input_json", "output_json", "params_json", "node_id", "reserved_credits", "charged_credits", "error", "created_at", "started_at", "finished_at"],
                before.Jobs, current.Jobs, x => x.Id,
                x => [x.Id, x.App, x.Type, x.UserId, x.ProjectId, x.Pool, x.Priority, x.Status, Json(x.Resources), Json(x.Input), JsonOrNull(x.Output), Json(x.Params), x.NodeId, x.ReservedCredits, x.ChargedCredits, x.Error, x.CreatedAt, x.StartedAt, x.FinishedAt]);

            await ApplyAsync(connection, tx, "job_events", "id",
                ["id", "job_id", "event_type", "payload_json", "created_at"],
                before.JobEvents, current.JobEvents, x => x.Id,
                x => [x.Id, x.JobId, x.EventType, Json(x.Payload), x.CreatedAt]);

            await ApplyAsync(connection, tx, "nodes", "id",
                ["id", "owner_id", "pool", "status", "gpu_model", "vram_gb", "region", "price_per_hour", "reputation", "api_key", "created_at", "approved_at", "last_heartbeat"],
                before.Nodes, current.Nodes, x => x.Id,
                x => [x.Id, x.OwnerId, x.Pool, x.Status, x.GpuModel, x.VramGb, x.Region, x.PricePerHour, x.Reputation, x.ApiKey, x.CreatedAt, x.ApprovedAt, x.LastHeartbeat]);

            await ApplyAsync(connection, tx, "pools", "id",
                ["id", "name", "type", "region", "status", "capacity_json", "policy_json", "created_at", "updated_at"],
                before.Pools, current.Pools, x => x.Id,
                x => [x.Id, x.Name, x.Type, x.Region, x.Status, Json(x.Capacity), Json(x.Policy), x.CreatedAt, x.UpdatedAt]);

            await ApplyAsync(connection, tx, "pricing_rules", "id",
                ["id", "name", "resource_type", "pool", "region", "unit_price", "billing_unit", "status", "effective_at", "updated_at"],
                before.PricingRules, current.PricingRules, x => x.Id,
                x => [x.Id, x.Name, x.ResourceType, x.Pool, x.Region, x.UnitPrice, x.BillingUnit, x.Status, x.EffectiveAt, x.UpdatedAt]);

            await ApplyAsync(connection, tx, "wallet_ledger", "id",
                ["id", "user_id", "delta", "reason", "job_id", "created_at"],
                before.WalletLedger, current.WalletLedger, x => x.Id,
                x => [x.Id, x.UserId, x.Delta, x.Reason, x.JobId, x.CreatedAt]);

            await ApplyAsync(connection, tx, "settlements", "id",
                ["id", "provider_id", "period", "node_count", "gross_amount", "platform_fee", "net_amount", "status", "created_at", "paid_at"],
                before.Settlements, current.Settlements, x => x.Id,
                x => [x.Id, x.ProviderId, x.Period, x.NodeCount, x.GrossAmount, x.PlatformFee, x.NetAmount, x.Status, x.CreatedAt, x.PaidAt]);

            await ApplyAsync(connection, tx, "api_tokens", "id",
                ["id", "user_id", "name", "token_hash", "token_prefix", "created_at", "last_used_at", "revoked_at"],
                before.ApiTokens, current.ApiTokens, x => x.Id,
                x => [x.Id, x.UserId, x.Name, x.TokenHash, x.TokenPrefix, x.CreatedAt, x.LastUsedAt, x.RevokedAt]);

            await ApplyAsync(connection, tx, "team_members", "id",
                ["id", "user_id", "email", "role", "status", "created_at"],
                before.TeamMembers, current.TeamMembers, x => x.Id,
                x => [x.Id, x.UserId, x.Email, x.Role, x.Status, x.CreatedAt]);

            await ApplyAsync(connection, tx, "account_devices", "id",
                ["id", "user_id", "name", "ip", "user_agent", "created_at", "last_seen_at", "revoked_at"],
                before.Devices, current.Devices, x => x.Id,
                x => [x.Id, x.UserId, x.Name, x.Ip, x.UserAgent, x.CreatedAt, x.LastSeenAt, x.RevokedAt]);

            await ApplyAsync(connection, tx, "dataset_exports", "id",
                ["id", "user_id", "project_id", "task_id", "status", "object_path", "size_bytes", "config_json", "created_at", "finished_at", "error_message", "download_url"],
                before.Exports, current.Exports, x => x.Id,
                x => [x.Id, x.UserId, x.ProjectId, x.TaskId, x.Status, x.Path, x.Size, Json(x.Config), x.CreatedAt, x.FinishedAt, x.ErrorMessage, x.DownloadUrl]);

            var quotas = StateDiff.Compare(before.Quotas, current.Quotas);
            await DeleteKeysAsync(connection, tx, "ai_quotas", "user_id", quotas.Deletes.Select(int.Parse));
            await UpsertRowsAsync(connection, tx, "ai_quotas",
                ["user_id", "plan", "daily_limit", "daily_used", "daily_reset_at"],
                quotas.Upserts.Select(pair => new object?[] { int.Parse(pair.Key), pair.Value.Plan, pair.Value.DailyLimit, pair.Value.DailyUsed, pair.Value.DailyResetAt.ToDateTime(TimeOnly.MinValue) }));

            var notifications = StateDiff.Compare(before.NotificationSettings, current.NotificationSettings);
            await DeleteKeysAsync(connection, tx, "notification_settings", "user_id", notifications.Deletes.Select(int.Parse));
            await UpsertRowsAsync(connection, tx, "notification_settings",
                ["user_id", "email_task", "email_billing", "browser_notice", "weekly_report", "updated_at"],
                notifications.Upserts.Select(pair => new object?[] { int.Parse(pair.Key), pair.Value.EmailTask, pair.Value.EmailBilling, pair.Value.BrowserNotice, pair.Value.WeeklyReport, pair.Value.UpdatedAt }));

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    async Task<bool> HasAnyDataAsync(MySqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM app_counters";
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    async Task<MaskFlowState?> TryLoadLegacyStateAsync(MySqlConnection connection)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'app_state'";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0) return null;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM app_state WHERE id = 1";
        var value = await command.ExecuteScalarAsync() as string;
        return string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<MaskFlowState>(value, json);
    }

    async Task LoadCountersAsync(MySqlConnection c, MaskFlowState s)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT next_user_id,next_file_id,next_event_id FROM app_counters WHERE id=1";
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return;
        s.NextUserId = r.GetInt32(0);
        s.NextFileId = r.GetInt32(1);
        s.NextEventId = r.GetInt32(2);
    }

    async Task LoadUsersAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM users", r =>
        s.Users.Add(new User(I(r, "id"), S(r, "email"), S(r, "password_hash"), S(r, "salt"), S(r, "plan"), L(r, "quota_bytes"), L(r, "used_bytes"), D(r, "created_at"), N(r, "username"), N(r, "phone"), N(r, "avatar_path"))));

    async Task LoadSessionsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM sessions", r =>
        s.Sessions.Add(new Session(S(r, "token"), I(r, "user_id"), D(r, "created_at"), D(r, "expires_at"), N(r, "device_id"))));

    async Task LoadProjectsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM projects", r =>
        s.Projects.Add(new Project(S(r, "id"), I(r, "user_id"), S(r, "name"), S(r, "description"), S(r, "data_type"), FromJson<SplitConfig>(S(r, "split_json"))!, I(r, "image_count"), I(r, "annotation_count"), D(r, "created_at"), D(r, "updated_at"))));

    async Task LoadProjectLabelsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM project_labels ORDER BY sort_order", r =>
    {
        var id = S(r, "project_id");
        if (!s.ProjectLabels.TryGetValue(id, out var labels))
        {
            labels = [];
            s.ProjectLabels[id] = labels;
        }
        labels.Add(S(r, "label_name"));
    });

    async Task LoadLabelPromptsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM project_label_prompts", r =>
    {
        var id = S(r, "project_id");
        if (!s.ProjectLabelPrompts.TryGetValue(id, out var prompts))
        {
            prompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            s.ProjectLabelPrompts[id] = prompts;
        }

        prompts[S(r, "label_name")] = S(r, "prompt");
    });

    async Task LoadAiModelsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM ai_models", r =>
        s.AiModels.Add(new AiModel(S(r, "id"), I(r, "user_id"), N(r, "project_id"), S(r, "name"), S(r, "object_path"), L(r, "size_bytes"), S(r, "sha256"), FromJson<List<string>>(N(r, "class_names_json")), D(r, "created_at"))));

    async Task LoadFilesAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM files", r =>
        s.Files.Add(new FileItem(I(r, "id"), I(r, "user_id"), N(r, "project_id"), S(r, "name"), S(r, "object_path"), L(r, "size_bytes"), S(r, "kind"), N(r, "content_type"), D(r, "created_at"), S(r, "download_url"), N(r, "phash"))));

    async Task LoadAnnotationSetsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM annotation_sets", r =>
        s.AnnotationSets.Add(new AnnotationSet(S(r, "id"), I(r, "user_id"), I(r, "file_id"), I(r, "width"), I(r, "height"), FromJson<List<AnnotationItem>>(S(r, "annotations_json")) ?? [], S(r, "yolo_txt"), D(r, "created_at"), D(r, "updated_at"),
            N(r, "review_status") ?? "draft", N(r, "review_comment"), NI(r, "reviewed_by"), ND(r, "reviewed_at"), NI(r, "submitted_by"))));

    async Task LoadProjectMembersAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM project_members", r =>
        s.ProjectMembers.Add(new ProjectMember(S(r, "id"), S(r, "project_id"), I(r, "user_id"), S(r, "role"), I(r, "invited_by"), D(r, "created_at"))));

    async Task LoadProjectInvitesAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM project_invites", r =>
        s.ProjectInvites.Add(new ProjectInvite(S(r, "id"), S(r, "project_id"), S(r, "email"), S(r, "role"), S(r, "token_hash"), I(r, "invited_by"), D(r, "created_at"), D(r, "expires_at"))));

    async Task LoadVideosAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM videos", r =>
        s.Videos.Add(new VideoItem(S(r, "id"), I(r, "user_id"), S(r, "project_id"), S(r, "name"), S(r, "object_path"), L(r, "size_bytes"),
            Db(r, "fps"), Db(r, "duration_sec"), Db(r, "every_seconds"), FromJson<List<int>>(S(r, "frame_file_ids_json")) ?? [],
            FromJson<List<int>>(S(r, "frame_indexes_json")) ?? [], S(r, "status"), N(r, "error"), D(r, "created_at"))));

    async Task LoadTrainingRunsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM training_runs", r =>
        s.TrainingRuns.Add(new TrainingRun(S(r, "id"), I(r, "user_id"), S(r, "project_id"), I(r, "created_by"), S(r, "name"), S(r, "task"), S(r, "format"),
            FromJson<TrainingRequest>(S(r, "request_json"))!, S(r, "status"), I(r, "epoch"), FromJson<List<Dictionary<string, double>>>(S(r, "history_json")) ?? [],
            N(r, "metrics_json") is { } metrics ? JsonDocument.Parse(metrics).RootElement.Clone() : null, FromJson<List<TrainingArtifact>>(S(r, "artifacts_json")) ?? [],
            N(r, "model_id"), I(r, "image_count"), N(r, "error"), D(r, "created_at"), ND(r, "started_at"), ND(r, "finished_at"))));

    async Task LoadFileAssignmentsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM file_assignments", r =>
        s.FileAssignments.Add(new FileAssignment(I(r, "file_id"), I(r, "user_id"), I(r, "assigned_by"), D(r, "assigned_at"))));

    async Task LoadTasksAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM tasks", r =>
        s.Tasks.Add(new TaskItem(S(r, "id"), I(r, "user_id"), S(r, "job_id"), S(r, "type"), N(r, "title"), N(r, "project_id"), NI(r, "file_id"), I(r, "image_count"), S(r, "status"), Db(r, "progress"), FromJson<Dictionary<string, object?>>(N(r, "result_json")), N(r, "error_message"), D(r, "created_at"), ND(r, "started_at"), ND(r, "finished_at"))));

    async Task LoadJobsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM jobs", r =>
        s.Jobs.Add(new Job(S(r, "id"), S(r, "app"), S(r, "type"), I(r, "user_id"), N(r, "project_id"), S(r, "pool"), S(r, "priority"), S(r, "status"), FromJson<Dictionary<string, object?>>(S(r, "resources_json")) ?? [], FromJson<Dictionary<string, object?>>(S(r, "input_json")) ?? [], FromJson<Dictionary<string, object?>>(N(r, "output_json")) , FromJson<Dictionary<string, object?>>(S(r, "params_json")) ?? [], N(r, "node_id"), NI(r, "reserved_credits"), NI(r, "charged_credits"), N(r, "error"), D(r, "created_at"), ND(r, "started_at"), ND(r, "finished_at"))));

    async Task LoadJobEventsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM job_events", r =>
        s.JobEvents.Add(new JobEvent(I(r, "id"), S(r, "job_id"), S(r, "event_type"), FromJson<Dictionary<string, object?>>(S(r, "payload_json")) ?? [], D(r, "created_at"))));

    async Task LoadNodesAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM nodes", r =>
        s.Nodes.Add(new Node(S(r, "id"), I(r, "owner_id"), S(r, "pool"), S(r, "status"), N(r, "gpu_model"), NI(r, "vram_gb"), N(r, "region"), NDb(r, "price_per_hour"), Db(r, "reputation"), S(r, "api_key"), D(r, "created_at"), ND(r, "approved_at"), ND(r, "last_heartbeat"))));

    async Task LoadPoolsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM pools", r =>
        s.Pools.Add(new Pool(S(r, "id"), S(r, "name"), S(r, "type"), N(r, "region"), S(r, "status"), FromJson<Dictionary<string, object?>>(S(r, "capacity_json")) ?? [], FromJson<Dictionary<string, object?>>(S(r, "policy_json")) ?? [], D(r, "created_at"), D(r, "updated_at"))));

    async Task LoadPricingRulesAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM pricing_rules", r =>
        s.PricingRules.Add(new PricingRule(S(r, "id"), S(r, "name"), S(r, "resource_type"), N(r, "pool"), N(r, "region"), Db(r, "unit_price"), S(r, "billing_unit"), S(r, "status"), D(r, "effective_at"), D(r, "updated_at"))));

    async Task LoadWalletLedgerAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM wallet_ledger", r =>
        s.WalletLedger.Add(new WalletEntry(I(r, "id"), I(r, "user_id"), I(r, "delta"), S(r, "reason"), N(r, "job_id"), D(r, "created_at"))));

    async Task LoadSettlementsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM settlements", r =>
        s.Settlements.Add(new Settlement(S(r, "id"), I(r, "provider_id"), S(r, "period"), I(r, "node_count"), Db(r, "gross_amount"), Db(r, "platform_fee"), Db(r, "net_amount"), S(r, "status"), D(r, "created_at"), ND(r, "paid_at"))));

    async Task LoadApiTokensAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM api_tokens", r =>
        s.ApiTokens.Add(new ApiToken(S(r, "id"), I(r, "user_id"), S(r, "name"), S(r, "token_hash"), S(r, "token_prefix"), D(r, "created_at"), ND(r, "last_used_at"), ND(r, "revoked_at"))));

    async Task LoadTeamMembersAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM team_members", r =>
        s.TeamMembers.Add(new TeamMember(S(r, "id"), I(r, "user_id"), S(r, "email"), S(r, "role"), S(r, "status"), D(r, "created_at"))));

    async Task LoadDevicesAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM account_devices", r =>
        s.Devices.Add(new AccountDevice(S(r, "id"), I(r, "user_id"), S(r, "name"), N(r, "ip"), N(r, "user_agent"), D(r, "created_at"), D(r, "last_seen_at"), ND(r, "revoked_at"))));

    async Task LoadExportsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM dataset_exports", r =>
        s.Exports.Add(new DatasetExport(S(r, "id"), I(r, "user_id"), N(r, "project_id"), N(r, "task_id"), S(r, "status"), N(r, "object_path"), L(r, "size_bytes"), FromJson<ExportRequest>(S(r, "config_json"))!, D(r, "created_at"), ND(r, "finished_at"), N(r, "error_message"), N(r, "download_url"))));

    async Task LoadQuotasAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM ai_quotas", r =>
        s.Quotas[I(r, "user_id").ToString()] = new AiQuota(S(r, "plan"), I(r, "daily_limit"), I(r, "daily_used"), DateOnly.FromDateTime(r.GetDateTime("daily_reset_at"))));

    async Task LoadNotificationsAsync(MySqlConnection c, MaskFlowState s) => await ReadAsync(c, "SELECT * FROM notification_settings", r =>
        s.NotificationSettings[I(r, "user_id").ToString()] = new NotificationSettings(B(r, "email_task"), B(r, "email_billing"), B(r, "browser_notice"), B(r, "weekly_report"), D(r, "updated_at")));

    async Task ReadAsync(MySqlConnection connection, string sql, Action<MySqlDataReader> read)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) read(reader);
    }

    const int RowsPerStatement = 200;

    async Task ApplyAsync<T, TKey>(MySqlConnection connection, MySqlTransaction tx, string table, string keyColumn, string[] columns,
        IEnumerable<T> previous, IEnumerable<T> current, Func<T, TKey> key, Func<T, object?[]> values)
        where T : class
        where TKey : notnull
    {
        var changes = StateDiff.Compare(previous, current, key);
        if (changes.IsEmpty) return;
        await DeleteKeysAsync(connection, tx, table, keyColumn, changes.Deletes.Cast<object>());
        await UpsertRowsAsync(connection, tx, table, columns, changes.Upserts.Select(values));
    }

    static async Task UpsertRowsAsync(MySqlConnection connection, MySqlTransaction tx, string table, string[] columns, IEnumerable<object?[]> rows)
    {
        var columnNames = string.Join(", ", columns.Select(column => $"`{column}`"));
        var updates = string.Join(", ", columns.Select(column => $"`{column}`=new_row.`{column}`"));
        foreach (var chunk in rows.Chunk(RowsPerStatement))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            var groups = new string[chunk.Length];
            for (var row = 0; row < chunk.Length; row++)
            {
                var placeholders = new string[columns.Length];
                for (var column = 0; column < columns.Length; column++)
                {
                    var name = $"@p{row}_{column}";
                    placeholders[column] = name;
                    command.Parameters.AddWithValue(name, chunk[row][column] ?? DBNull.Value);
                }

                groups[row] = $"({string.Join(", ", placeholders)})";
            }

            command.CommandText = $"INSERT INTO `{table}` ({columnNames}) VALUES {string.Join(", ", groups)} AS new_row ON DUPLICATE KEY UPDATE {updates}";
            await command.ExecuteNonQueryAsync();
        }
    }

    static async Task DeleteKeysAsync<TKey>(MySqlConnection connection, MySqlTransaction tx, string table, string keyColumn, IEnumerable<TKey> keys)
    {
        foreach (var chunk in keys.Chunk(RowsPerStatement * 5))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            var placeholders = chunk.Select((_, i) => "@k" + i).ToArray();
            command.CommandText = $"DELETE FROM `{table}` WHERE `{keyColumn}` IN ({string.Join(", ", placeholders)})";
            for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue("@k" + i, chunk[i]);
            await command.ExecuteNonQueryAsync();
        }
    }

    static async Task ApplyProjectLabelsAsync(MySqlConnection connection, MySqlTransaction tx,
        IReadOnlyDictionary<string, List<string>> previous, IReadOnlyDictionary<string, List<string>> current)
    {
        var changes = StateDiff.Compare(previous, current);
        await DeleteKeysAsync(connection, tx, "project_labels", "project_id", changes.Deletes);
        foreach (var (projectId, labels) in changes.Upserts)
        {
            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM `project_labels` WHERE `project_id` = @project_id";
                clear.Parameters.AddWithValue("@project_id", projectId);
                await clear.ExecuteNonQueryAsync();
            }

            await UpsertRowsAsync(connection, tx, "project_labels",
                ["project_id", "label_name", "sort_order"],
                labels.Select((label, index) => new object?[] { projectId, label, index }));
        }
    }

    static async Task ApplyLabelPromptsAsync(MySqlConnection connection, MySqlTransaction tx,
        IReadOnlyDictionary<string, Dictionary<string, string>> previous, IReadOnlyDictionary<string, Dictionary<string, string>> current)
    {
        var changes = StateDiff.Compare(previous, current);
        await DeleteKeysAsync(connection, tx, "project_label_prompts", "project_id", changes.Deletes.Concat(changes.Upserts.Select(x => x.Key)));
        await UpsertRowsAsync(connection, tx, "project_label_prompts",
            ["project_id", "label_name", "prompt"],
            changes.Upserts.SelectMany(pair => pair.Value.Select(prompt => new object?[] { pair.Key, prompt.Key, prompt.Value })));
    }

    static async Task ExecAsync(MySqlConnection connection, MySqlTransaction? tx, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    string Json<T>(T value) => JsonSerializer.Serialize(value, json);
    string? JsonOrNull<T>(T? value) => value is null ? null : JsonSerializer.Serialize(value, json);
    T? FromJson<T>(string? value) => string.IsNullOrWhiteSpace(value) ? default : JsonSerializer.Deserialize<T>(value, json);

    static string S(MySqlDataReader r, string name) => r.GetString(name);
    static string? N(MySqlDataReader r, string name) => r.IsDBNull(name) ? null : r.GetString(name);
    static int I(MySqlDataReader r, string name) => r.GetInt32(name);
    static int? NI(MySqlDataReader r, string name) => r.IsDBNull(name) ? null : r.GetInt32(name);
    static long L(MySqlDataReader r, string name) => r.GetInt64(name);
    static double Db(MySqlDataReader r, string name) => r.GetDouble(name);
    static double? NDb(MySqlDataReader r, string name) => r.IsDBNull(name) ? null : r.GetDouble(name);
    static bool B(MySqlDataReader r, string name) => r.GetBoolean(name);
    static DateTimeOffset D(MySqlDataReader r, string name) => new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(name), DateTimeKind.Utc));
    static DateTimeOffset? ND(MySqlDataReader r, string name) => r.IsDBNull(name) ? null : D(r, name);

    static readonly string[] SchemaSql =
    [
        """
        CREATE TABLE IF NOT EXISTS app_counters (
          id INT PRIMARY KEY,
          next_user_id INT NOT NULL,
          next_file_id INT NOT NULL,
          next_event_id INT NOT NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS users (
          id INT PRIMARY KEY,
          email VARCHAR(255) NOT NULL UNIQUE,
          password_hash VARCHAR(255) NOT NULL,
          salt VARCHAR(64) NOT NULL,
          plan VARCHAR(32) NOT NULL,
          quota_bytes BIGINT NOT NULL,
          used_bytes BIGINT NOT NULL,
          created_at DATETIME(6) NOT NULL,
          username VARCHAR(120) NULL,
          phone VARCHAR(40) NULL,
          avatar_path VARCHAR(1024) NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS sessions (
          token VARCHAR(255) PRIMARY KEY,
          user_id INT NOT NULL,
          created_at DATETIME(6) NOT NULL,
          expires_at DATETIME(6) NOT NULL,
          INDEX ix_sessions_user_id (user_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        "ALTER TABLE sessions ADD COLUMN expires_at DATETIME(6) NOT NULL DEFAULT (DATE_ADD(NOW(), INTERVAL 7 DAY))",
        "ALTER TABLE sessions ADD COLUMN device_id VARCHAR(64) NULL",
        """
        CREATE TABLE IF NOT EXISTS projects (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          name VARCHAR(200) NOT NULL,
          description TEXT NOT NULL,
          data_type VARCHAR(64) NOT NULL,
          split_json JSON NOT NULL,
          image_count INT NOT NULL,
          annotation_count INT NOT NULL,
          created_at DATETIME(6) NOT NULL,
          updated_at DATETIME(6) NOT NULL,
          INDEX ix_projects_user_id (user_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS project_labels (
          project_id VARCHAR(64) NOT NULL,
          label_name VARCHAR(120) NOT NULL,
          sort_order INT NOT NULL,
          PRIMARY KEY (project_id, label_name)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS files (
          id INT PRIMARY KEY,
          user_id INT NOT NULL,
          project_id VARCHAR(64) NULL,
          name VARCHAR(255) NOT NULL,
          object_path VARCHAR(1200) NOT NULL,
          size_bytes BIGINT NOT NULL,
          kind VARCHAR(32) NOT NULL,
          content_type VARCHAR(120) NULL,
          created_at DATETIME(6) NOT NULL,
          download_url VARCHAR(500) NOT NULL,
          INDEX ix_files_user_project (user_id, project_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS annotation_sets (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          file_id INT NOT NULL,
          width INT NOT NULL,
          height INT NOT NULL,
          annotations_json JSON NOT NULL,
          yolo_txt MEDIUMTEXT NOT NULL,
          created_at DATETIME(6) NOT NULL,
          updated_at DATETIME(6) NOT NULL,
          UNIQUE KEY ux_annotation_file_user (user_id, file_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS tasks (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          job_id VARCHAR(64) NOT NULL,
          type VARCHAR(80) NOT NULL,
          title VARCHAR(255) NULL,
          project_id VARCHAR(64) NULL,
          file_id INT NULL,
          image_count INT NOT NULL,
          status VARCHAR(40) NOT NULL,
          progress DOUBLE NOT NULL,
          result_json JSON NULL,
          error_message TEXT NULL,
          created_at DATETIME(6) NOT NULL,
          started_at DATETIME(6) NULL,
          finished_at DATETIME(6) NULL,
          INDEX ix_tasks_user_project (user_id, project_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS jobs (
          id VARCHAR(64) PRIMARY KEY,
          app VARCHAR(80) NOT NULL,
          type VARCHAR(120) NOT NULL,
          user_id INT NOT NULL,
          project_id VARCHAR(64) NULL,
          pool VARCHAR(120) NOT NULL,
          priority VARCHAR(40) NOT NULL,
          status VARCHAR(40) NOT NULL,
          resources_json JSON NOT NULL,
          input_json JSON NOT NULL,
          output_json JSON NULL,
          params_json JSON NOT NULL,
          node_id VARCHAR(64) NULL,
          reserved_credits INT NULL,
          charged_credits INT NULL,
          error TEXT NULL,
          created_at DATETIME(6) NOT NULL,
          started_at DATETIME(6) NULL,
          finished_at DATETIME(6) NULL,
          INDEX ix_jobs_status (status)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS job_events (
          id INT PRIMARY KEY,
          job_id VARCHAR(64) NOT NULL,
          event_type VARCHAR(80) NOT NULL,
          payload_json JSON NOT NULL,
          created_at DATETIME(6) NOT NULL,
          INDEX ix_job_events_job_id (job_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS nodes (
          id VARCHAR(64) PRIMARY KEY,
          owner_id INT NOT NULL,
          pool VARCHAR(120) NOT NULL,
          status VARCHAR(40) NOT NULL,
          gpu_model VARCHAR(120) NULL,
          vram_gb INT NULL,
          region VARCHAR(80) NULL,
          price_per_hour DOUBLE NULL,
          reputation DOUBLE NOT NULL,
          api_key VARCHAR(255) NOT NULL,
          created_at DATETIME(6) NOT NULL,
          approved_at DATETIME(6) NULL,
          last_heartbeat DATETIME(6) NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS pools (
          id VARCHAR(64) PRIMARY KEY,
          name VARCHAR(160) NOT NULL,
          type VARCHAR(80) NOT NULL,
          region VARCHAR(80) NULL,
          status VARCHAR(40) NOT NULL,
          capacity_json JSON NOT NULL,
          policy_json JSON NOT NULL,
          created_at DATETIME(6) NOT NULL,
          updated_at DATETIME(6) NOT NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS pricing_rules (
          id VARCHAR(64) PRIMARY KEY,
          name VARCHAR(160) NOT NULL,
          resource_type VARCHAR(80) NOT NULL,
          pool VARCHAR(120) NULL,
          region VARCHAR(80) NULL,
          unit_price DOUBLE NOT NULL,
          billing_unit VARCHAR(40) NOT NULL,
          status VARCHAR(40) NOT NULL,
          effective_at DATETIME(6) NOT NULL,
          updated_at DATETIME(6) NOT NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS wallet_ledger (
          id INT PRIMARY KEY,
          user_id INT NOT NULL,
          delta INT NOT NULL,
          reason VARCHAR(255) NOT NULL,
          job_id VARCHAR(64) NULL,
          created_at DATETIME(6) NOT NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS settlements (
          id VARCHAR(64) PRIMARY KEY,
          provider_id INT NOT NULL,
          period VARCHAR(40) NOT NULL,
          node_count INT NOT NULL,
          gross_amount DOUBLE NOT NULL,
          platform_fee DOUBLE NOT NULL,
          net_amount DOUBLE NOT NULL,
          status VARCHAR(40) NOT NULL,
          created_at DATETIME(6) NOT NULL,
          paid_at DATETIME(6) NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS api_tokens (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          name VARCHAR(120) NOT NULL,
          token_hash VARCHAR(255) NOT NULL,
          token_prefix VARCHAR(20) NOT NULL,
          created_at DATETIME(6) NOT NULL,
          last_used_at DATETIME(6) NULL,
          revoked_at DATETIME(6) NULL,
          INDEX ix_api_tokens_user_id (user_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS team_members (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          email VARCHAR(255) NOT NULL,
          role VARCHAR(40) NOT NULL,
          status VARCHAR(40) NOT NULL,
          created_at DATETIME(6) NOT NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS account_devices (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          name VARCHAR(160) NOT NULL,
          ip VARCHAR(80) NULL,
          user_agent TEXT NULL,
          created_at DATETIME(6) NOT NULL,
          last_seen_at DATETIME(6) NOT NULL,
          revoked_at DATETIME(6) NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS dataset_exports (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          project_id VARCHAR(64) NULL,
          task_id VARCHAR(64) NULL,
          status VARCHAR(40) NOT NULL,
          object_path VARCHAR(1200) NULL,
          size_bytes BIGINT NOT NULL,
          config_json JSON NOT NULL,
          created_at DATETIME(6) NOT NULL,
          finished_at DATETIME(6) NULL,
          error_message TEXT NULL,
          download_url VARCHAR(500) NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS ai_quotas (
          user_id INT PRIMARY KEY,
          plan VARCHAR(32) NOT NULL,
          daily_limit INT NOT NULL,
          daily_used INT NOT NULL,
          daily_reset_at DATE NOT NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS project_label_prompts (
          project_id VARCHAR(64) NOT NULL,
          label_name VARCHAR(120) NOT NULL,
          prompt VARCHAR(200) NOT NULL,
          PRIMARY KEY (project_id, label_name)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS ai_models (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          project_id VARCHAR(64) NULL,
          name VARCHAR(200) NOT NULL,
          object_path VARCHAR(1200) NOT NULL,
          size_bytes BIGINT NOT NULL,
          sha256 CHAR(64) NOT NULL,
          class_names_json JSON NULL,
          created_at DATETIME(6) NOT NULL,
          INDEX ix_ai_models_user_id (user_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS notification_settings (
          user_id INT PRIMARY KEY,
          email_task BOOLEAN NOT NULL,
          email_billing BOOLEAN NOT NULL,
          browser_notice BOOLEAN NOT NULL,
          weekly_report BOOLEAN NOT NULL,
          updated_at DATETIME(6) NOT NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS project_members (
          id VARCHAR(100) PRIMARY KEY,
          project_id VARCHAR(64) NOT NULL,
          user_id INT NOT NULL,
          role VARCHAR(20) NOT NULL,
          invited_by INT NOT NULL,
          created_at DATETIME(6) NOT NULL,
          INDEX ix_project_members_user (user_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS project_invites (
          id VARCHAR(64) PRIMARY KEY,
          project_id VARCHAR(64) NOT NULL,
          email VARCHAR(255) NOT NULL,
          role VARCHAR(20) NOT NULL,
          token_hash CHAR(64) NOT NULL,
          invited_by INT NOT NULL,
          created_at DATETIME(6) NOT NULL,
          expires_at DATETIME(6) NOT NULL
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS file_assignments (
          file_id INT PRIMARY KEY,
          user_id INT NOT NULL,
          assigned_by INT NOT NULL,
          assigned_at DATETIME(6) NOT NULL,
          INDEX ix_file_assignments_user (user_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS videos (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          project_id VARCHAR(64) NOT NULL,
          name VARCHAR(255) NOT NULL,
          object_path VARCHAR(1200) NOT NULL,
          size_bytes BIGINT NOT NULL,
          fps DOUBLE NOT NULL,
          duration_sec DOUBLE NOT NULL,
          every_seconds DOUBLE NOT NULL,
          frame_file_ids_json JSON NOT NULL,
          frame_indexes_json JSON NOT NULL,
          status VARCHAR(20) NOT NULL,
          error TEXT NULL,
          created_at DATETIME(6) NOT NULL,
          INDEX ix_videos_project (project_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        """
        CREATE TABLE IF NOT EXISTS training_runs (
          id VARCHAR(64) PRIMARY KEY,
          user_id INT NOT NULL,
          project_id VARCHAR(64) NOT NULL,
          created_by INT NOT NULL,
          name VARCHAR(255) NOT NULL,
          task VARCHAR(20) NOT NULL,
          format VARCHAR(40) NOT NULL,
          request_json JSON NOT NULL,
          status VARCHAR(20) NOT NULL,
          epoch INT NOT NULL,
          history_json JSON NOT NULL,
          metrics_json JSON NULL,
          artifacts_json JSON NOT NULL,
          model_id VARCHAR(64) NULL,
          image_count INT NOT NULL,
          error TEXT NULL,
          created_at DATETIME(6) NOT NULL,
          started_at DATETIME(6) NULL,
          finished_at DATETIME(6) NULL,
          INDEX ix_training_runs_project (project_id)
        ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
        """,
        // Column additions go last so their tables exist on a fresh database.
        "ALTER TABLE files ADD COLUMN phash CHAR(16) NULL",
        "ALTER TABLE annotation_sets ADD COLUMN review_status VARCHAR(20) NOT NULL DEFAULT 'draft'",
        "ALTER TABLE annotation_sets ADD COLUMN review_comment VARCHAR(1000) NULL",
        "ALTER TABLE annotation_sets ADD COLUMN reviewed_by INT NULL",
        "ALTER TABLE annotation_sets ADD COLUMN reviewed_at DATETIME(6) NULL",
        "ALTER TABLE annotation_sets ADD COLUMN submitted_by INT NULL"
    ];
}
