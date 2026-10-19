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
