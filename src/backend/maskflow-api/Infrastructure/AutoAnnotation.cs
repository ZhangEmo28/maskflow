using System.Text.Json;

public static class AutoAnnotationParser
{
    // Converts the SAM /api/annotation/masks response into annotation items.
    // Ids are regenerated so repeated runs never collide with existing annotations.
    public static AnnotationSaveRequest Parse(int fileId, string body, string? defaultLabel, IReadOnlyList<string> projectLabels)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var width = root.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
        var height = root.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;
        var items = new List<AnnotationItem>();
        var assignedLabel = string.IsNullOrWhiteSpace(defaultLabel) ? null : defaultLabel.Trim();
        var classId = MaskFlowStore.ResolveLabelClassId(assignedLabel, projectLabels);
        if (assignedLabel is not null && classId < 0)
        {
            assignedLabel = null;
            classId = -1;
        }

        if (root.TryGetProperty("masks", out var masks) && masks.ValueKind == JsonValueKind.Array)
        {
            foreach (var mask in masks.EnumerateArray())
            {
                if (!mask.TryGetProperty("yoloBox", out var box)) continue;
                List<double>? segment = null;
                if (mask.TryGetProperty("yoloSegments", out var segments) && segments.ValueKind == JsonValueKind.Array)
                {
                    segment = PickLargestSegment(segments.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.Array)
                        .Select(x => x.EnumerateArray().Select(value => value.GetDouble()).ToList()));
                }

                var confidence = mask.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number
                    ? score.GetDouble()
                    : 1.0;
                items.Add(new AnnotationItem(
                    "ann_" + Util.Id(),
                    classId,
                    assignedLabel,
                    new YoloBox(
                        box.TryGetProperty("cx", out var cx) ? cx.GetDouble() : 0,
                        box.TryGetProperty("cy", out var cy) ? cy.GetDouble() : 0,
                        box.TryGetProperty("width", out var bw) ? bw.GetDouble() : 0,
                        box.TryGetProperty("height", out var bh) ? bh.GetDouble() : 0),
                    segment,
                    confidence));
            }
        }

        return new AnnotationSaveRequest(fileId, width, height, items);
    }

    // SAM 3 concept mode: prompts were sent in project-label order, so promptIndex == classId.
    public static AnnotationSaveRequest ParseConcepts(int fileId, string body, IReadOnlyList<string> projectLabels)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var items = new List<AnnotationItem>();
        foreach (var mask in Array(root, "masks"))
        {
            var index = mask.TryGetProperty("promptIndex", out var prompt) && prompt.ValueKind == JsonValueKind.Number ? prompt.GetInt32() : -1;
            var label = index >= 0 && index < projectLabels.Count ? projectLabels[index] : null;
            items.Add(ReadItem(mask, label is null ? -1 : index, label));
        }

        return new AnnotationSaveRequest(fileId, Int(root, "width"), Int(root, "height"), DeduplicateAcrossLabels(items));
    }

    // Custom YOLO model: class names are matched to project labels by name (case-insensitive);
    // unmatched classes stay unassigned so they show up for review instead of being dropped.
    public static AnnotationSaveRequest ParseModelDetections(int fileId, string body, IReadOnlyList<string> projectLabels, out List<string> classNames)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        classNames = Array(root, "classNames").Select(x => x.GetString() ?? "").ToList();
        var items = new List<AnnotationItem>();
        foreach (var detection in Array(root, "detections"))
        {
            var className = detection.TryGetProperty("className", out var name) ? name.GetString() : null;
            var classId = MaskFlowStore.ResolveLabelClassId(className, projectLabels);
            items.Add(ReadItem(detection, classId, classId >= 0 ? projectLabels[classId] : null));
        }

        return new AnnotationSaveRequest(fileId, Int(root, "width"), Int(root, "height"), items);
    }

    // The same object can match two prompts ("car" and "truck"); keep the higher-scoring one.
    public static List<AnnotationItem> DeduplicateAcrossLabels(IEnumerable<AnnotationItem> items, double iou = 0.85)
    {
        var kept = new List<AnnotationItem>();
        foreach (var item in items.OrderByDescending(x => x.Confidence))
        {
            if (kept.Any(other => AnnotationMerge.Iou(other.Bbox, item.Bbox) >= iou)) continue;
            kept.Add(item);
        }

        return kept;
    }

    static AnnotationItem ReadItem(JsonElement element, int classId, string? label)
    {
        var box = element.GetProperty("yoloBox");
        List<double>? segment = null;
        if (element.TryGetProperty("yoloSegments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            segment = PickLargestSegment(segments.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Array)
                .Select(x => x.EnumerateArray().Select(value => value.GetDouble()).ToList()));
        }

        return new AnnotationItem(
            "ann_" + Util.Id(),
            classId,
            label,
            new YoloBox(Double(box, "cx"), Double(box, "cy"), Double(box, "width"), Double(box, "height")),
            segment,
            element.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetDouble() : 1.0);
    }

    static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    static double Double(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    // A mask split by occlusion yields several contours; keep the one with the largest area.
    public static List<double>? PickLargestSegment(IEnumerable<List<double>> segments)
    {
        List<double>? best = null;
        var bestArea = 0.0;
        foreach (var segment in segments)
        {
            if (segment.Count < 6 || segment.Count % 2 != 0) continue;
            var area = PolygonArea(segment);
            if (best is null || area > bestArea)
            {
                best = segment;
                bestArea = area;
            }
        }

        return best;
    }

    public static double PolygonArea(IReadOnlyList<double> points)
    {
        double area = 0;
        for (var i = 0; i < points.Count; i += 2)
        {
            var next = (i + 2) % points.Count;
            area += points[i] * points[next + 1] - points[next] * points[i + 1];
        }

        return Math.Abs(area) / 2;
    }
}

public static class AnnotationMerge
{
    public const double DuplicateIou = 0.7;

    // Re-running AI keeps every human-confirmed target and only replaces unconfirmed ones.
    // New detections that overlap a kept target are dropped as duplicates.
    public static List<AnnotationItem> KeepConfirmed(IEnumerable<AnnotationItem>? existing, IEnumerable<AnnotationItem> incoming, double duplicateIou = DuplicateIou)
    {
        var kept = (existing ?? []).Where(x => x.Confirmed).ToList();
        var merged = new List<AnnotationItem>(kept);
        foreach (var item in incoming)
        {
            if (kept.Any(confirmed => Iou(confirmed.Bbox, item.Bbox) >= duplicateIou)) continue;
            merged.Add(item);
        }

        return merged;
    }

    public static double Iou(YoloBox a, YoloBox b)
    {
        var left = Math.Max(a.Cx - a.Width / 2, b.Cx - b.Width / 2);
        var top = Math.Max(a.Cy - a.Height / 2, b.Cy - b.Height / 2);
        var right = Math.Min(a.Cx + a.Width / 2, b.Cx + b.Width / 2);
        var bottom = Math.Min(a.Cy + a.Height / 2, b.Cy + b.Height / 2);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
