// Snapshots share unchanged row objects by reference (records are immutable and every change
// replaces the record), so "changed" means "a different object under the same key".
public sealed record RowChanges<T, TKey>(IReadOnlyList<T> Upserts, IReadOnlyList<TKey> Deletes)
{
    public bool IsEmpty => Upserts.Count == 0 && Deletes.Count == 0;
}

public static class StateDiff
{
    public static RowChanges<T, TKey> Compare<T, TKey>(IEnumerable<T> previous, IEnumerable<T> current, Func<T, TKey> key)
        where T : class
        where TKey : notnull
    {
        var before = new Dictionary<TKey, T>();
        foreach (var row in previous)
        {
            before[key(row)] = row;
        }

        var upserts = new List<T>();
        var seen = new HashSet<TKey>();
        foreach (var row in current)
        {
            var rowKey = key(row);
            seen.Add(rowKey);
            if (!before.TryGetValue(rowKey, out var old) || !ReferenceEquals(old, row))
            {
                upserts.Add(row);
            }
        }

        var deletes = before.Keys.Where(rowKey => !seen.Contains(rowKey)).ToList();
        return new RowChanges<T, TKey>(upserts, deletes);
    }

    public static RowChanges<KeyValuePair<string, TValue>, string> Compare<TValue>(
        IReadOnlyDictionary<string, TValue> previous,
        IReadOnlyDictionary<string, TValue> current)
        where TValue : class
    {
        var upserts = current
            .Where(pair => !previous.TryGetValue(pair.Key, out var old) || !ReferenceEquals(old, pair.Value))
            .ToList();
        var deletes = previous.Keys.Where(key => !current.ContainsKey(key)).ToList();
        return new RowChanges<KeyValuePair<string, TValue>, string>(upserts, deletes);
    }

    // Shallow copy-on-write snapshot: new collections, shared (immutable) rows.
    public static MaskFlowState Snapshot(MaskFlowState state) => state with
    {
        Users = [.. state.Users],
        Sessions = [.. state.Sessions],
        Projects = [.. state.Projects],
        Files = [.. state.Files],
        AnnotationSets = [.. state.AnnotationSets],
        Tasks = [.. state.Tasks],
        Jobs = [.. state.Jobs],
        JobEvents = [.. state.JobEvents],
        Nodes = [.. state.Nodes],
        Pools = [.. state.Pools],
        PricingRules = [.. state.PricingRules],
        WalletLedger = [.. state.WalletLedger],
        Settlements = [.. state.Settlements],
        ApiTokens = [.. state.ApiTokens],
        TeamMembers = [.. state.TeamMembers],
        Devices = [.. state.Devices],
        Exports = [.. state.Exports],
        AiModels = [.. state.AiModels],
        ProjectMembers = [.. state.ProjectMembers],
        ProjectInvites = [.. state.ProjectInvites],
        FileAssignments = [.. state.FileAssignments],
        Videos = [.. state.Videos],
        TrainingRuns = [.. state.TrainingRuns],
        OAuthClients = [.. state.OAuthClients],
        OAuthGrants = [.. state.OAuthGrants],
        ProjectLabels = new(state.ProjectLabels),
        ProjectLabelPrompts = new(state.ProjectLabelPrompts),
        Quotas = new(state.Quotas),
        NotificationSettings = new(state.NotificationSettings)
    };
}
