using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

public sealed record AutoAnnotationOptions(string Mode, double Conf, string? DefaultLabel, string? ModelId)
{
    // everything: SAM "segment everything", every mask gets DefaultLabel.
    // concepts:   SAM 3 text prompts, one per project label, so each mask gets its own label.
    // model:      a user-uploaded ONNX YOLO model; class names are matched to project labels.
    public const string Everything = "everything";
    public const string Concepts = "concepts";
    public const string Model = "model";

    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "concepts" or "text" => Concepts,
        "model" or "yolo" => Model,
        _ => Everything
    };

    public static AutoAnnotationOptions Create(string? mode, double conf, string? defaultLabel, string? modelId) =>
        new(NormalizeMode(mode), conf, string.IsNullOrWhiteSpace(defaultLabel) ? null : defaultLabel.Trim(), string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim());
}

public sealed class SamServiceException(int statusCode, string body, string? contentType)
    : Exception($"SAM inference service returned HTTP {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
    public string? ContentType { get; } = contentType;

    public string Detail
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(Body);
                return document.RootElement.TryGetProperty("detail", out var detail) ? detail.ToString() : Body;
            }
            catch (JsonException)
            {
                return Body;
            }
        }
    }
}

public sealed class AutoAnnotationService(MaskFlowStore store, IHttpClientFactory clientFactory)
{
    public List<string> Validate(int userId, FileItem file, AutoAnnotationOptions options)
    {
        if (string.IsNullOrWhiteSpace(file.ProjectId))
        {
            throw new BadHttpRequestException("File must belong to a project before auto annotation.", 400);
        }

        var labels = store.GetProjectLabels(userId, file.ProjectId);
        if (labels.Count == 0)
        {
            throw new BadHttpRequestException("Add at least one label to the project before running auto annotation.", 400);
        }

        if (options.Mode == AutoAnnotationOptions.Everything && options.DefaultLabel is not null
            && MaskFlowStore.ResolveLabelClassId(options.DefaultLabel, labels) < 0)
        {
            throw new BadHttpRequestException("DefaultLabel is not in project labels.", 400);
        }

        if (options.Mode == AutoAnnotationOptions.Model && FindModel(userId, options.ModelId) is null)
        {
            throw new BadHttpRequestException("Model not found.", 404);
        }

        return labels;
    }

    public AiModel? FindModel(int userId, string? modelId) =>
        modelId is null ? null : store.State.AiModels.FirstOrDefault(x => x.Id == modelId && x.UserId == userId);

    public async Task<AnnotationSet> RunAsync(int userId, FileItem file, AutoAnnotationOptions options, bool recordTask, CancellationToken cancellationToken)
    {
        var labels = Validate(userId, file, options);
        var client = clientFactory.CreateClient("sam");
        var conf = options.Conf.ToString(CultureInfo.InvariantCulture);
        AnnotationSaveRequest request;
        switch (options.Mode)
        {
            case AutoAnnotationOptions.Concepts:
            {
                var prompts = labels.Select(label => store.PromptForLabel(file.ProjectId!, label)).ToList();
                var body = await PostImageAsync(client, "/api/annotation/concepts", file,
                    new() { ["prompts"] = JsonSerializer.Serialize(prompts), ["conf"] = conf }, cancellationToken);
                request = AutoAnnotationParser.ParseConcepts(file.Id, body, labels);
                break;
            }
            case AutoAnnotationOptions.Model:
            {
                var model = FindModel(userId, options.ModelId)!;
                await EnsureModelCachedAsync(client, model, cancellationToken);
                var body = await PostImageAsync(client, "/api/yolo/predict", file,
                    new() { ["model_id"] = model.Id, ["conf"] = conf }, cancellationToken);
                request = AutoAnnotationParser.ParseModelDetections(file.Id, body, labels, out var classNames);
                if (classNames.Count > 0 && (model.ClassNames is null || !model.ClassNames.SequenceEqual(classNames)))
                {
                    await store.UpdateAiModelClassNamesAsync(model.Id, classNames);
                }

                break;
            }
            default:
            {
                var body = await PostImageAsync(client, "/api/annotation/masks", file, new() { ["conf"] = conf }, cancellationToken);
                request = AutoAnnotationParser.Parse(file.Id, body, options.DefaultLabel, labels);
                break;
            }
        }

        var (set, _) = await store.PersistAutoAnnotationAsync(userId, request, file, recordTask: recordTask);
        return set;
    }

    async Task<string> PostImageAsync(HttpClient client, string path, FileItem file, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        await using var stream = await store.OpenStoredObjectAsync(file.Path);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? "application/octet-stream");
        content.Add(fileContent, "image", file.Name);
        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value), name);
        }

        using var response = await client.PostAsync(path, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SamServiceException((int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
        }

        return body;
    }

    // The inference service keeps its own cache of model files; upload only when missing or stale.
    async Task EnsureModelCachedAsync(HttpClient client, AiModel model, CancellationToken cancellationToken)
    {
        using (var status = await client.GetAsync($"/api/yolo/models/{model.Id}", cancellationToken))
        {
            if (status.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;
                if (root.TryGetProperty("cached", out var cached) && cached.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("sha256", out var sha) && sha.GetString() == model.Sha256)
                {
                    return;
                }
            }
        }

        using var content = new MultipartFormDataContent();
        await using var stream = await store.OpenStoredObjectAsync(model.Path);
        content.Add(new StreamContent(stream), "weights", $"{model.Id}.onnx");
        content.Add(new StringContent(model.Sha256), "sha256");
        using var response = await client.PutAsync($"/api/yolo/models/{model.Id}", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SamServiceException((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken), response.Content.Headers.ContentType?.ToString());
        }
    }

    public async Task DropCachedModelAsync(string modelId)
    {
        try
        {
            using var response = await clientFactory.CreateClient("sam").DeleteAsync($"/api/yolo/models/{modelId}");
        }
        catch (HttpRequestException)
        {
            // The inference service may be offline; its cache entry is harmless and evicted on restart.
        }
    }
}
