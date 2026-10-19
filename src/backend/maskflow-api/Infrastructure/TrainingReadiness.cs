// Checks whether a project's annotations are enough to train on, before a GPU is spent on it:
// hard minimums (blockers), how each class actually lands in train/val/test, and concrete advice
// on how many more images of which kind to add.
public sealed record ReadinessClass(string Label, int Images, int Instances, int Train, int Val, int Test, int Recommended, int AddImages, double? MedianArea, string? Advice);

public sealed record TrainingReadinessReport(
    bool Ok, List<string> Blockers, List<string> Warnings, List<string> Suggestions, List<ReadinessClass> Classes,
    int Images, int BackgroundImages, int UnannotatedImages, int Train, int Val, int Test, SplitConfig Split, object Thresholds);

public static class TrainingReadiness
{
    static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    // Below these, training cannot produce a usable model or a meaningful validation score.
    public static int MinImages => Env("MASKFLOW_TRAIN_MIN_IMAGES", 20);
    public static int MinImagesPerClass => Env("MASKFLOW_TRAIN_MIN_PER_CLASS", 10);
    // Advice only: a class with fewer images usually gives an unstable model.
    public static int RecommendedPerClass => Env("MASKFLOW_TRAIN_RECOMMENDED_PER_CLASS", 50);
    public const double SmallObjectArea = 0.01;      // median box under 1% of the image
    public const double MaxSplitDeviation = 0.15;    // 15 percentage points from the requested ratio

    // Same rule as the trainer service's infer_task, so the check works without reaching it.
    public static string TaskFromWeightName(string? name)
    {
        var lower = (name ?? "").ToLowerInvariant();
        return lower.Contains("-seg") ? "segment" : lower.Contains("-cls") ? "classify" : lower.Contains("-pose") ? "pose" : lower.Contains("-obb") ? "obb" : "detect";
    }

    public static TrainingReadinessReport Evaluate(DatasetExportPlan plan, SplitConfig split, int seed)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var suggestions = new List<string>();
        var classify = plan.Format == "classification-crops";
        var assignments = DatasetSplitter.Assign(plan.Samples, split, seed, plan.GroupOf);
        var background = plan.Files.Count(x => plan.Annotations[x.Id].Annotations.Count == 0);
        var unannotated = plan.AllFiles.Count - plan.Annotations.Count;
        int Count(string name) => assignments.Values.Count(x => x == name);

        // For classification each crop is a sample, so classes are counted in targets, not images.
        var counts = plan.Labels.ToDictionary(x => x, x => classify
            ? plan.Samples.Sum(s => s.LabelCounts.GetValueOrDefault(x))
            : plan.Samples.Count(s => s.LabelCounts.ContainsKey(x)), StringComparer.OrdinalIgnoreCase);
        var largest = counts.Count == 0 ? 0 : counts.Values.Max();
        var unit = classify ? "个目标" : "张图片";

        if (plan.Labels.Count == 0) blockers.Add("项目还没有标签，请先添加类别。");
        if (plan.Files.Count < MinImages)
        {
            blockers.Add($"可训练的已标注图片只有 {plan.Files.Count} 张，至少需要 {MinImages} 张（AI 未检出、尚未标注的图片不算）。");
        }

        if (split.Val <= 0) blockers.Add("训练需要验证集，请把验证集比例设为大于 0。");
        else if (plan.Files.Count > 0 && Count("val") == 0) blockers.Add("按当前比例划分后验证集为空，请增加图片或提高验证集比例。");
        if (plan.Files.Count > 0 && Count("train") == 0) blockers.Add("按当前比例划分后训练集为空。");

        var classes = new List<ReadinessClass>();
        foreach (var label in plan.Labels)
        {
            var withLabel = plan.Samples.Where(s => s.LabelCounts.ContainsKey(label)).ToList();
            var images = counts[label];
            var instances = withLabel.Sum(s => s.LabelCounts[label]);
            int InSplit(string name) => classify
                ? withLabel.Where(s => assignments.GetValueOrDefault(s.FileId) == name).Sum(s => s.LabelCounts[label])
                : withLabel.Count(s => assignments.GetValueOrDefault(s.FileId) == name);
            var (train, val, test) = (InSplit("train"), InSplit("val"), InSplit("test"));
            var areas = withLabel
                .SelectMany(s => plan.Annotations[s.FileId].Annotations
                    .Where(a => MaskFlowStore.IsExportableAnnotation(a) && string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Bbox.Width * a.Bbox.Height))
                .OrderBy(x => x)
                .ToList();
            double? median = areas.Count == 0 ? null : areas[areas.Count / 2];
            // Enough on its own, and not far behind the largest class (within 3:1).
            var recommended = Math.Max(RecommendedPerClass, (int)Math.Ceiling(largest / 3.0));
            var add = Math.Max(0, recommended - images);

            var advice = new List<string>();
            if (images == 0)
            {
                blockers.Add($"类别「{label}」还没有任何{(classify ? "目标" : "图片")}，模型无法学习它；请补充或删除这个标签。");
            }
            else if (images < MinImagesPerClass)
            {
                blockers.Add($"类别「{label}」只有 {images} {unit}，每个类别至少需要 {MinImagesPerClass} {unit}。");
            }

            if (images > 0 && train == 0) blockers.Add($"类别「{label}」被全部分到了验证/测试集，训练集中没有它。");
            if (images > 0 && split.Val > 0 && val == 0)
            {
                warnings.Add($"类别「{label}」在验证集中没有{(classify ? "目标" : "图片")}，训练后无法评估这个类别的效果。");
            }

            if (images >= MinImagesPerClass)
            {
                var trainShare = train / (double)images;
                if (Math.Abs(trainShare - split.Train / 100.0) > MaxSplitDeviation)
                {
                    warnings.Add($"类别「{label}」的划分偏离了设定比例：训练 {Percent(train, images)} / 验证 {Percent(val, images)} / 测试 {Percent(test, images)}，"
                                 + $"目标 {split.Train}/{split.Val}/{split.Test}。通常是相似图片必须分在同一组造成的，补充不同场景的图片可以改善。");
                }
            }

            if (median is < SmallObjectArea) advice.Add($"目标普遍很小（中位面积约占图片 {median.Value:P1}），补充时优先选目标更大、更清晰的近景图，或把训练图片尺寸调到 960 以上");
            var similar = withLabel.Count(s => plan.GroupOf.ContainsKey(s.FileId));
            if (withLabel.Count >= 5 && similar > withLabel.Count / 2) advice.Add("现有图片多为相似画面（同一机位或视频连续帧），补充不同场景、角度、光照、距离的图片");
            if (add > 0)
            {
                var why = images < RecommendedPerClass ? $"每类建议至少 {RecommendedPerClass} {unit}" : $"与最多的类别（{largest}）差距超过 3 倍";
                suggestions.Add($"「{label}」现有 {images} {unit}，建议再补充约 {add} {(classify ? "个该类目标" : $"张包含「{label}」的图片")}（{why}）"
                                + (advice.Count > 0 ? $"；{string.Join("；", advice)}。" : "。"));
            }
            else if (advice.Count > 0)
            {
                suggestions.Add($"「{label}」：{string.Join("；", advice)}。");
            }

            classes.Add(new ReadinessClass(label, images, instances, train, val, test, recommended, add, median is null ? null : Math.Round(median.Value, 5),
                advice.Count == 0 ? null : string.Join("；", advice)));
        }

        var present = counts.Values.Where(x => x > 0).ToList();
        if (present.Count >= 2 && present.Max() > present.Min() * 3)
        {
            warnings.Add($"类别数量不平衡：最多 {present.Max()}、最少 {present.Min()} {unit}（超过 3:1），少数类别容易被漏检。");
        }

        if (!classify)
        {
            if (background == 0 && plan.Files.Count > 0)
            {
                suggestions.Add($"可加入约 {Math.Max(1, (int)Math.Ceiling(plan.Files.Count * 0.05))}–{Math.Max(2, (int)Math.Ceiling(plan.Files.Count * 0.1))} 张同场景但不含任何目标的背景图，在标注页点「标为背景图」，能减少误检。");
            }
            else if (background > plan.Files.Count / 4)
            {
                warnings.Add($"背景图（无目标）有 {background} 张，占 {Percent(background, plan.Files.Count)}，超过 25% 可能让模型过于保守、漏检增多。");
            }
        }

        var targets = plan.Annotations.Values.SelectMany(x => x.Annotations).Where(MaskFlowStore.IsExportableAnnotation).ToList();
        var unconfirmed = targets.Count(x => !x.Confirmed);
        if (!plan.ConfirmedOnly && targets.Count > 0 && unconfirmed > targets.Count * 0.3)
        {
            warnings.Add($"{Percent(unconfirmed, targets.Count)} 的目标是尚未人工确认的 AI 结果，错误标注会直接教给模型；建议先复核，或勾选「只用人工确认过的标注」。");
        }

        var unassigned = plan.Annotations.Values.Sum(x => x.Annotations.Count(a => !MaskFlowStore.IsExportableAnnotation(a)));
        if (unassigned > 0) warnings.Add($"有 {unassigned} 个目标没有类别，训练时会被忽略。");
        if (unannotated > 0) warnings.Add($"还有 {unannotated} 张图片未标注（包括 AI 没有检测到目标的），不会参与训练。");
        if (plan.ConfirmedOnly && plan.ExcludedUnconfirmed > 0) warnings.Add($"只用已确认标注：{plan.ExcludedUnconfirmed} 个未确认目标不参与训练。");
        if (plan.OversizedSimilarGroups.Count > 0)
        {
            warnings.Add($"有 {plan.OversizedSimilarGroups.Count} 组超大相似图片组（最大 {plan.OversizedSimilarGroups[0]} 张），验证集可能与训练集过于相似，评估会偏乐观。");
        }

        return new TrainingReadinessReport(blockers.Count == 0, blockers, warnings, suggestions, classes, plan.Files.Count, background, unannotated,
            Count("train"), Count("val"), Count("test"), split,
            new { minImages = MinImages, minImagesPerClass = MinImagesPerClass, recommendedPerClass = RecommendedPerClass });
    }

    static string Percent(int part, int whole) => whole == 0 ? "0%" : $"{Math.Round(part * 100.0 / whole)}%";
}

public sealed partial class MaskFlowStore
{
    public async Task<TrainingReadinessReport> CheckTrainingDataAsync(ProjectAccess access, TrainingRequest request, string task)
    {
        var split = request.Split ?? new SplitConfig(70, 20, 10);
        DatasetSplitter.Validate(split);
        var format = TrainingFormat(task, access.Project.DataType);
        await EnsurePerceptualHashesAsync(access.OwnerId, access.Project.Id);
        var plan = DatasetExportPlan.Create(State, access.OwnerId, new ExportRequest(access.Project.Id, null, split, format, request.Seed, request.ConfirmedOnly));
        var report = TrainingReadiness.Evaluate(plan, split, request.Seed);
        // Problems the export itself would reject (e.g. segmentation targets without polygons) are blockers too.
        try
        {
            plan.ValidateExport();
        }
        catch (BadHttpRequestException ex) when (!report.Blockers.Contains(ex.Message))
        {
            report.Blockers.Add(ex.Message);
            report = report with { Ok = false };
        }

        return report;
    }
}
