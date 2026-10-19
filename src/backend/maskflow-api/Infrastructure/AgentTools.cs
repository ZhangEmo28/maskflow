using SkiaSharp;

// Helpers behind the MCP tools: drawing annotations for an agent to look at, rule-based quality
// checks, and detection matching for model evaluation. Pure functions over state and bytes.

public static class AnnotationRenderer
{
    // Same palette as the web editor (lib/labelColors.js), indexed by the label's position.
    static readonly SKColor[] Palette =
    [
        new(0x25, 0x63, 0xeb), new(0x16, 0xa3, 0x4a), new(0xdc, 0x26, 0x26), new(0x7c, 0x3a, 0xed),
        new(0xdb, 0x27, 0x77), new(0x0d, 0x94, 0x88), new(0xea, 0x58, 0x0c), new(0x08, 0x91, 0xb2),
        new(0x65, 0xa3, 0x0d), new(0x4f, 0x46, 0xe5), new(0xe1, 0x1d, 0x48), new(0xca, 0x8a, 0x04)
    ];

    static readonly SKColor Unassigned = new(0x94, 0xa3, 0xb8);
    static readonly SKColor Prediction = new(0xfa, 0xcc, 0x15);

    // 3x5 digit glyphs: tags must render without system fonts (the Linux image ships none, and
    // label names may be Chinese), so targets are numbered and named in the text legend instead.
    static readonly string[][] Digits =
    [
        ["111", "101", "101", "101", "111"], ["010", "110", "010", "010", "111"], ["111", "001", "111", "100", "111"],
        ["111", "001", "111", "001", "111"], ["101", "101", "111", "001", "001"], ["111", "100", "111", "001", "111"],
        ["111", "100", "111", "101", "111"], ["111", "001", "010", "010", "010"], ["111", "101", "111", "101", "111"],
        ["111", "101", "111", "001", "111"]
    ];

    public static SKColor ColorFor(string? label, IReadOnlyList<string> labels)
    {
        var index = MaskFlowStore.ResolveLabelClassId(label, labels);
        return index < 0 ? Unassigned : Palette[index % Palette.Length];
    }

    // Targets are drawn in list order and tagged 1..n; predictions (if any) are yellow and tagged
    // with a dark background. Confirmed targets have solid outlines, unconfirmed ones dashed.
    public static byte[] Render(byte[] imageBytes, IReadOnlyList<AnnotationItem> annotations, IReadOnlyList<string> labels,
        int maxSide = 1024, IReadOnlyList<AnnotationItem>? predictions = null)
    {
        using var source = ImageTools.DecodeOriented(imageBytes);
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        var stroke = Math.Max(2f, Math.Min(width, height) / 320f);
        for (var i = 0; i < annotations.Count; i++)
        {
            var item = annotations[i];
            DrawTarget(canvas, item, ColorFor(item.Label, labels), width, height, stroke, dashed: !item.Confirmed, i + 1, SKColors.White, null);
        }

        for (var i = 0; i < (predictions?.Count ?? 0); i++)
        {
            DrawTarget(canvas, predictions![i], Prediction, width, height, stroke, dashed: true, i + 1, Prediction, new SKColor(0x11, 0x18, 0x27));
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    static void DrawTarget(SKCanvas canvas, AnnotationItem item, SKColor color, int width, int height, float stroke, bool dashed, int number, SKColor digitColor, SKColor? tagColor)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true };
        if (dashed) paint.PathEffect = SKPathEffect.CreateDash([stroke * 3, stroke * 2], 0);
        if (item.Segment is { Count: >= 6 } segment)
        {
            using var builder = new SKPathBuilder();
            builder.MoveTo((float)(segment[0] * width), (float)(segment[1] * height));
            for (var i = 2; i + 1 < segment.Count; i += 2) builder.LineTo((float)(segment[i] * width), (float)(segment[i + 1] * height));
            builder.Close();
            using var path = builder.Detach();
            using var fill = new SKPaint { Color = color.WithAlpha(56), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, paint);
        }

        var box = item.Bbox;
        var rect = SKRect.Create((float)((box.Cx - box.Width / 2) * width), (float)((box.Cy - box.Height / 2) * height),
            (float)(box.Width * width), (float)(box.Height * height));
        canvas.DrawRect(rect, paint);
        DrawTag(canvas, number, rect.Left, rect.Top, Math.Max(2f, stroke), tagColor ?? color, digitColor, width, height);
    }

    static void DrawTag(SKCanvas canvas, int number, float x, float y, float pixel, SKColor background, SKColor foreground, int width, int height)
    {
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tagWidth = (text.Length * 4 + 1) * pixel;
        var tagHeight = 7 * pixel;
        // Keep the tag inside the image; above the box when there is room.
        var left = Math.Clamp(x, 0, Math.Max(0, width - tagWidth));
        var top = Math.Clamp(y - tagHeight, 0, Math.Max(0, height - tagHeight));
        using var fill = new SKPaint { Color = background, Style = SKPaintStyle.Fill };
        canvas.DrawRect(SKRect.Create(left, top, tagWidth, tagHeight), fill);
        using var ink = new SKPaint { Color = foreground, Style = SKPaintStyle.Fill };
        for (var c = 0; c < text.Length; c++)
        {
            var glyph = Digits[text[c] - '0'];
            for (var row = 0; row < 5; row++)
            {
                for (var col = 0; col < 3; col++)
                {
                    if (glyph[row][col] == '1')
                    {
                        canvas.DrawRect(SKRect.Create(left + (1 + c * 4 + col) * pixel, top + (1 + row) * pixel, pixel, pixel), ink);
                    }
                }
            }
        }
    }
}

public sealed record QualityIssue(string Kind, string Severity, int? FileId, string? FileName, string? AnnotationId, string Message);

public static class QualityCheck
{
    public const double TinyArea = 0.0004;      // < 2% x 2% of the image
    public const double HugeArea = 0.9;         // likely a background / whole-image mask
    public const double DuplicateIou = 0.9;
    public const double ConflictIou = 0.85;
    public const double LowConfidence = 0.5;
    public const int MinImagesPerLabel = 10;

    public static object Run(MaskFlowState state, Project project, IReadOnlyList<string> labels, int maxIssues = 80)
    {
        var ownerId = project.UserId;
        var segmentation = MaskFlowStore.NormalizeDataType(project.DataType) == "segmentation";
        var files = state.Files.Where(x => x.UserId == ownerId && x.ProjectId == project.Id && x.Kind == "image").OrderBy(x => x.Id).ToList();
        var sets = state.AnnotationSets.Where(x => x.UserId == ownerId).ToDictionary(x => x.FileId);
        var issues = new List<QualityIssue>();
        var labelImages = labels.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        var labelTargets = labels.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        int unannotated = 0, empty = 0, targets = 0, confirmed = 0;

        foreach (var file in files)
        {
            if (!sets.TryGetValue(file.Id, out var set) || !MaskFlowStore.IsAnnotated(set))
            {
                unannotated++;
                continue;
            }

            var items = set.Annotations;
            if (items.Count == 0) empty++;
            if (set.ReviewStatus == "rejected")
            {
                issues.Add(new("review_rejected", "warning", file.Id, file.Name, null, $"审核被驳回：{set.ReviewComment}"));
            }

            foreach (var label in items.Where(MaskFlowStore.IsExportableAnnotation).Select(x => x.Label!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (labelImages.ContainsKey(label)) labelImages[label]++;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                targets++;
                if (item.Confirmed) confirmed++;
                var box = item.Bbox;
                var area = box.Width * box.Height;
                if (MaskFlowStore.IsExportableAnnotation(item) && labelTargets.ContainsKey(item.Label!)) labelTargets[item.Label!]++;
                if (!MaskFlowStore.IsExportableAnnotation(item))
                {
                    issues.Add(new("unlabeled", "error", file.Id, file.Name, item.Id, "目标没有类别，导出和训练时会被忽略。"));
                }

                if (box.Width <= 0 || box.Height <= 0 || box.Cx - box.Width / 2 < -0.01 || box.Cx + box.Width / 2 > 1.01
                    || box.Cy - box.Height / 2 < -0.01 || box.Cy + box.Height / 2 > 1.01)
                {
                    issues.Add(new("invalid_box", "error", file.Id, file.Name, item.Id, "框的大小为 0 或超出图片范围。"));
                }
                else if (area < TinyArea)
                {
                    issues.Add(new("tiny_box", "warning", file.Id, file.Name, item.Id, $"框非常小（占图片 {area:P2}），可能是噪点或误检。"));
                }
                else if (area > HugeArea)
                {
                    issues.Add(new("huge_box", "warning", file.Id, file.Name, item.Id, $"框几乎覆盖整张图（{area:P0}），可能把背景当成了目标。"));
                }

                if (!item.Confirmed && item.Confidence < LowConfidence)
                {
                    issues.Add(new("low_confidence", "warning", file.Id, file.Name, item.Id, $"AI 置信度只有 {item.Confidence:0.00}，尚未人工确认。"));
                }

                if (segmentation && MaskFlowStore.IsExportableAnnotation(item) && !MaskFlowStore.HasValidSegment(item))
                {
                    issues.Add(new("missing_segment", "error", file.Id, file.Name, item.Id, "分割项目中的目标缺少有效多边形，导出分割数据时会报错。"));
                }

                for (var j = i + 1; j < items.Count; j++)
                {
                    var other = items[j];
                    var iou = AnnotationMerge.Iou(item.Bbox, other.Bbox);
                    var sameLabel = string.Equals(item.Label, other.Label, StringComparison.OrdinalIgnoreCase);
                    if (sameLabel && iou >= DuplicateIou)
                    {
                        issues.Add(new("duplicate", "error", file.Id, file.Name, other.Id, $"与目标 {item.Id} 重复（同类别，IoU {iou:0.00}）。"));
                    }
                    else if (!sameLabel && iou >= ConflictIou && MaskFlowStore.IsExportableAnnotation(item) && MaskFlowStore.IsExportableAnnotation(other))
                    {
                        issues.Add(new("label_conflict", "warning", file.Id, file.Name, other.Id, $"与目标 {item.Id} 几乎重合但类别不同（{item.Label} / {other.Label}，IoU {iou:0.00}），可能同一物体标了两次。"));
                    }
                }
            }
        }

        foreach (var (label, count) in labelImages)
        {
            if (count == 0) issues.Add(new("label_missing", "error", null, null, null, $"类别「{label}」还没有任何标注。"));
            else if (count < MinImagesPerLabel) issues.Add(new("label_sparse", "warning", null, null, null, $"类别「{label}」只出现在 {count} 张图片中，建议至少 {MinImagesPerLabel} 张，否则模型很难学会。"));
        }

        var present = labelImages.Values.Where(x => x > 0).ToList();
        if (present.Count >= 2 && present.Max() > present.Min() * 10)
        {
            issues.Add(new("imbalance", "warning", null, null, null, $"类别严重不平衡：最多 {present.Max()} 张、最少 {present.Min()} 张图片。"));
        }

        var bySeverity = issues.GroupBy(x => x.Severity).ToDictionary(x => x.Key, x => x.Count());
        return new
        {
            images = files.Count,
            unannotatedImages = unannotated,
            backgroundImages = empty,
            targets,
            confirmedTargets = confirmed,
            unconfirmedTargets = targets - confirmed,
            labels = labels.Select(label => new { label, images = labelImages[label], targets = labelTargets[label] }),
            errorCount = bySeverity.GetValueOrDefault("error"),
            warningCount = bySeverity.GetValueOrDefault("warning"),
            issueCounts = issues.GroupBy(x => x.Kind).ToDictionary(x => x.Key, x => x.Count()),
            issues = issues.OrderBy(x => x.Severity == "error" ? 0 : 1).Take(maxIssues),
            truncated = issues.Count > maxIssues
        };
    }
}

public sealed record ClassMatch(string Label, int TruePositives, int FalsePositives, int FalseNegatives)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 0 : (double)TruePositives / (TruePositives + FalsePositives);
    public double Recall => TruePositives + FalseNegatives == 0 ? 0 : (double)TruePositives / (TruePositives + FalseNegatives);
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

public static class DetectionMatching
{
    // Greedy matching in descending confidence: a prediction is a true positive when it overlaps an
    // unmatched ground-truth target of the same class with IoU >= threshold (the usual mAP@0.5 rule).
    public static List<ClassMatch> Match(IReadOnlyList<AnnotationItem> truth, IReadOnlyList<AnnotationItem> predictions, double iouThreshold = 0.5)
    {
        var labels = truth.Concat(predictions).Where(MaskFlowStore.IsExportableAnnotation)
            .Select(x => x.Label!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<ClassMatch>();
        foreach (var label in labels)
        {
            var gt = truth.Where(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase)).ToList();
            var matched = new bool[gt.Count];
            int tp = 0, fp = 0;
            foreach (var prediction in predictions.Where(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Confidence))
            {
                var best = -1;
                var bestIou = iouThreshold;
                for (var i = 0; i < gt.Count; i++)
                {
                    if (matched[i]) continue;
                    var iou = AnnotationMerge.Iou(gt[i].Bbox, prediction.Bbox);
                    if (iou >= bestIou)
                    {
                        best = i;
                        bestIou = iou;
                    }
                }

                if (best >= 0)
                {
                    matched[best] = true;
                    tp++;
                }
                else
                {
                    fp++;
                }
            }

            result.Add(new ClassMatch(label, tp, fp, matched.Count(x => !x)));
        }

        return result;
    }

    public static List<ClassMatch> Sum(IEnumerable<ClassMatch> matches) =>
        matches.GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ClassMatch(g.First().Label, g.Sum(x => x.TruePositives), g.Sum(x => x.FalsePositives), g.Sum(x => x.FalseNegatives)))
            .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed partial class MaskFlowStore
{
    // Marks labelled targets as human-confirmed in one write. Filters are optional and combine:
    // specific images, specific labels, and a minimum AI confidence.
    public async Task<(int Targets, int Images)> ConfirmAnnotationsAsync(int ownerId, string projectId, IReadOnlyCollection<int>? fileIds,
        IReadOnlyCollection<string>? labels, double minConfidence)
    {
        var targets = 0;
        var images = 0;
        await SaveAsync(() =>
        {
            var projectFiles = State.Files.Where(x => x.UserId == ownerId && x.ProjectId == projectId).Select(x => x.Id).ToHashSet();
            for (var i = 0; i < State.AnnotationSets.Count; i++)
            {
                var set = State.AnnotationSets[i];
                if (set.UserId != ownerId || !projectFiles.Contains(set.FileId) || (fileIds is { Count: > 0 } && !fileIds.Contains(set.FileId))) continue;
                var changed = 0;
                var annotations = set.Annotations.Select(item =>
                {
                    if (item.Confirmed || !IsExportableAnnotation(item) || item.Confidence < minConfidence
                        || (labels is { Count: > 0 } && !labels.Contains(item.Label!, StringComparer.OrdinalIgnoreCase))) return item;
                    changed++;
                    return item with { Confirmed = true };
                }).ToList();
                if (changed == 0) continue;
                State.AnnotationSets[i] = set with { Annotations = annotations, UpdatedAt = DateTimeOffset.UtcNow };
                targets += changed;
                images++;
            }
        });
        return (targets, images);
    }
}

public sealed partial class MaskFlowStore
{
    // ---- Bulk operations on selected images (one write each) ----

    public async Task<int> DeleteFilesAsync(int ownerId, string projectId, IReadOnlyCollection<int> fileIds)
    {
        var deleted = 0;
        await SaveAsync(() =>
        {
            foreach (var fileId in fileIds.Distinct())
            {
                var file = State.Files.FirstOrDefault(x => x.Id == fileId && x.UserId == ownerId && x.ProjectId == projectId);
                if (file is not null && DeleteFileCoreLocked(ownerId, fileId)) deleted++;
            }
        });
        return deleted;
    }

    // "all": the images become unannotated. "unconfirmed": AI results awaiting review are removed and
    // human-confirmed targets stay; an image left without targets becomes unannotated again.
    public async Task<(int Images, int Targets)> ClearAnnotationsAsync(int ownerId, string projectId, IReadOnlyCollection<int> fileIds, bool unconfirmedOnly)
    {
        var images = 0;
        var targets = 0;
        await SaveAsync(() =>
        {
            var selected = State.Files.Where(x => x.UserId == ownerId && x.ProjectId == projectId && fileIds.Contains(x.Id)).Select(x => x.Id).ToHashSet();
            foreach (var set in State.AnnotationSets.Where(x => x.UserId == ownerId && selected.Contains(x.FileId)).ToList())
            {
                var kept = unconfirmedOnly ? set.Annotations.Where(x => x.Confirmed).ToList() : [];
                var removed = set.Annotations.Count - kept.Count;
                if (removed == 0 && (unconfirmedOnly || !set.Background)) continue;
                if (kept.Count == 0)
                {
                    State.AnnotationSets.Remove(set);
                }
                else
                {
                    ApplyAnnotationSet(ownerId, new AnnotationSaveRequest(set.FileId, set.Width, set.Height, kept));
                }

                images++;
                targets += removed;
            }
        });
        return (images, targets);
    }
}
