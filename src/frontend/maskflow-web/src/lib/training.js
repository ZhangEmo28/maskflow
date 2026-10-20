// Pure helpers for the training page (no Vue or network), shared with tests.

export const trainingStatusLabels = {
  queued: "排队中",
  preparing: "准备数据",
  training: "训练中",
  evaluating: "评估中",
  exporting: "导出模型",
  completed: "已完成",
  failed: "失败",
  cancelled: "已取消"
};

export const weightTaskLabels = { detect: "检测", segment: "分割", classify: "分类", pose: "姿态", obb: "旋转框" };

const activeStatuses = ["queued", "preparing", "training", "evaluating", "exporting"];

export function isActiveTraining(run) {
  return activeStatuses.includes(run?.status);
}

// Mirrors MaskFlowStore.TrainingFormat: which base weights can train on this project.
export function weightCompatibility(weight, dataType) {
  if (weight.task === "detect" || weight.task === "classify") return { ok: true, reason: "" };
  if (weight.task === "segment") return dataType === "segmentation" ? { ok: true, reason: "" } : { ok: false, reason: "需要分割项目" };
  return { ok: false, reason: "暂不支持" };
}

// Chart series from Ultralytics results.csv columns: losses, and mAP (or accuracy for classification).
export function trainingCurves(history = [], task = "detect") {
  const keys = Object.keys(history[0] || {});
  const lossKeys = keys.filter((key) => key.endsWith("_loss"));
  const metricKeys = task === "classify"
    ? keys.filter((key) => key.startsWith("metrics/accuracy"))
    : keys.filter((key) => /^metrics\/mAP50(-95)?\((B|M)\)$/.test(key));
  const series = (names) => names.map((name) => ({
    name,
    points: history.map((row, index) => [row.epoch ?? index + 1, row[name]]).filter(([, value]) => Number.isFinite(value))
  })).filter((item) => item.points.length);
  return { losses: series(lossKeys), metrics: series(metricKeys) };
}

// Overall validation/test metrics as table rows; keys follow Ultralytics' results_dict.
export function metricRows(split, task = "detect") {
  const overall = split?.overall || {};
  const suffix = task === "segment" ? "(M)" : "(B)";
  const rows = task === "classify"
    ? [["Top-1 准确率", "metrics/accuracy_top1"], ["Top-5 准确率", "metrics/accuracy_top5"]]
    : [["mAP50", `metrics/mAP50${suffix}`], ["mAP50-95", `metrics/mAP50-95${suffix}`], ["精确率 Precision", `metrics/precision${suffix}`], ["召回率 Recall", `metrics/recall${suffix}`]];
  return rows.filter(([, key]) => Number.isFinite(overall[key])).map(([label, key]) => ({ label, value: overall[key] }));
}

const IMAGE_EXTENSIONS = /\.(png|jpe?g)$/i;

function baseName(path) {
  return path.split("/").pop();
}

// Groups the result images Ultralytics produces into sections for the gallery. Prediction samples
// come as pairs (…_labels.jpg = human labels, …_pred.jpg = model output) and are shown side by side.
export function groupResultImages(artifacts = []) {
  const images = artifacts.map((item) => item.name).filter((name) => IMAGE_EXTENSIONS.test(name));
  const used = new Set();
  const take = (predicate) => images.filter((name) => !used.has(name) && predicate(name)).map((name) => {
    used.add(name);
    return name;
  });
  // Validation before test, and the normalized matrix (easier to read) before raw counts.
  const confusionRank = (name) => (name.startsWith("test/") ? 2 : 0) + (/normalized/i.test(name) ? 0 : 1);
  const pairs = (split) => take((name) => name.startsWith(`${split}/`) && /_batch\d+_pred\.jpe?g$/i.test(name)).map((pred) => {
    const labels = pred.replace(/_pred(\.jpe?g)$/i, "_labels$1");
    if (images.includes(labels)) used.add(labels);
    return { pred, labels: images.includes(labels) ? labels : null, title: baseName(pred).replace(/_pred\.jpe?g$/i, "") };
  });

  const sections = [
    { key: "predictions", title: "预测效果（验证集：左为人工标注，右为模型预测）", pairs: pairs("val") },
    { key: "testPredictions", title: "预测效果（测试集）", pairs: pairs("test") },
    // Ultralytics also validates once at the end of training and keeps those samples in train/.
    { key: "trainValPredictions", title: "训练结束时的验证样例（左为人工标注，右为模型预测）", pairs: pairs("train") },
    { key: "curves", title: "训练曲线", items: take((name) => /(^|\/)results\.png$/i.test(name)) },
    { key: "confusion", title: "混淆矩阵", items: take((name) => /confusion_matrix/i.test(baseName(name))).sort((a, b) => confusionRank(a) - confusionRank(b)) },
    { key: "metricCurves", title: "PR / F1 / 精确率 / 召回率曲线", items: take((name) => /(PR|F1|P|R)_curve\.png$/i.test(baseName(name))) },
    { key: "labels", title: "标签分布", items: take((name) => /^labels/i.test(baseName(name))) },
    { key: "trainBatches", title: "训练样本（数据增强后）", items: take((name) => /^train_batch/i.test(baseName(name))) },
    { key: "other", title: "其他图片", items: take(() => true) }
  ];
  return sections.filter((section) => (section.pairs || section.items).length);
}

// Human-readable names for the files of a run.
export function describeArtifact(name) {
  const known = {
    "dataset.zip": "本次训练的数据集",
    "train/weights/best.pt": "最佳权重 best.pt（推荐）",
    "train/weights/last.pt": "最后一轮权重 last.pt",
    "train/weights/best.onnx": "ONNX 模型（可用于自动标注）",
    "train/results.csv": "每轮指标 results.csv",
    "train/args.yaml": "完整训练参数 args.yaml",
    "metrics.json": "评估结果 metrics.json",
    "train.log": "训练日志",
    "environment.json": "软硬件环境"
  };
  if (known[name]) return known[name];
  if (name.startsWith("base/")) return `基础权重 ${baseName(name)}`;
  return name;
}
