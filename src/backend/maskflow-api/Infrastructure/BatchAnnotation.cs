using System.Text.Json;

// Job input and task result are stored as JSON: values are CLR objects right after creation
// and JsonElement after a reload from MySQL, so both go through SerializeToElement.
public sealed record BatchTaskInput(List<int> FileIds, string? Mode, double Conf, string? DefaultLabel, string? ModelId)
{
    public static BatchTaskInput? From(Dictionary<string, object?>? input)
    {
        if (input is null) return null;
        var root = JsonSerializer.SerializeToElement(input);
        if (!root.TryGetProperty("fileIds", out var ids) || ids.ValueKind != JsonValueKind.Array) return null;
        return new BatchTaskInput(
            ids.EnumerateArray().Select(x => x.GetInt32()).ToList(),
            String(root, "mode"),
            root.TryGetProperty("conf", out var conf) && conf.ValueKind == JsonValueKind.Number ? conf.GetDouble() : 0.25,
            String(root, "defaultLabel"),
            String(root, "modelId"));
    }

    static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

// NoTargets: images where AI ran fine but found nothing (they stay unannotated).
public sealed record BatchProgress(int Processed, int Succeeded, int Failed, string? LastError, int NoTargets = 0)
{
    public static readonly BatchProgress Empty = new(0, 0, 0, null);

    public Dictionary<string, object?> ToResult(int total) => new()
    {
        ["total"] = total,
        ["processed"] = Processed,
        ["succeeded"] = Succeeded,
        ["failed"] = Failed,
        ["noTargets"] = NoTargets,
        ["lastError"] = LastError
    };

    public static BatchProgress From(Dictionary<string, object?>? result)
    {
        if (result is null) return Empty;
        var root = JsonSerializer.SerializeToElement(result);
        int Int(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        var lastError = root.TryGetProperty("lastError", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        return new BatchProgress(Int("processed"), Int("succeeded"), Int("failed"), lastError, Int("noTargets"));
    }
}
