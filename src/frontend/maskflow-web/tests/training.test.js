import test from "node:test";
import assert from "node:assert/strict";
import { metricRows, trainingCurves, weightCompatibility } from "../src/lib/training.js";

test("only offers weights that match the project type", () => {
  assert.equal(weightCompatibility({ task: "detect" }, "detection").ok, true);
  assert.equal(weightCompatibility({ task: "classify" }, "detection").ok, true);
  assert.deepEqual(weightCompatibility({ task: "segment" }, "detection"), { ok: false, reason: "需要分割项目" });
  assert.equal(weightCompatibility({ task: "segment" }, "segmentation").ok, true);
  assert.equal(weightCompatibility({ task: "pose" }, "detection").ok, false);
});

test("splits results.csv columns into loss and mAP curves", () => {
  const history = [
    { epoch: 1, "train/box_loss": 1.2, "val/box_loss": 1.4, "metrics/mAP50(B)": 0.1, "metrics/mAP50-95(B)": 0.05, "metrics/precision(B)": 0.3, lr: 0.01 },
    { epoch: 2, "train/box_loss": 0.9, "val/box_loss": 1.1, "metrics/mAP50(B)": 0.3, "metrics/mAP50-95(B)": 0.15, "metrics/precision(B)": 0.5, lr: 0.009 }
  ];
  const { losses, metrics } = trainingCurves(history, "detect");
  assert.deepEqual(losses.map((x) => x.name), ["train/box_loss", "val/box_loss"]);
  assert.deepEqual(metrics.map((x) => x.name), ["metrics/mAP50(B)", "metrics/mAP50-95(B)"]);
  assert.deepEqual(metrics[0].points, [[1, 0.1], [2, 0.3]]);
  assert.equal(trainingCurves([{ epoch: 1, "metrics/accuracy_top1": 0.8 }], "classify").metrics[0].name, "metrics/accuracy_top1");
});

test("reads mask metrics for segmentation runs", () => {
  const rows = metricRows({ overall: { "metrics/mAP50(M)": 0.6, "metrics/mAP50-95(M)": 0.4, "metrics/mAP50(B)": 0.9 } }, "segment");
  assert.deepEqual(rows, [{ label: "mAP50", value: 0.6 }, { label: "mAP50-95", value: 0.4 }]);
});

import { describeArtifact, groupResultImages } from "../src/lib/training.js";

test("groups result images and pairs predictions with labels", () => {
  const names = [
    "dataset.zip", "train/weights/best.pt", "train/results.png", "train/labels.jpg", "train/train_batch0.jpg",
    "val/confusion_matrix.png", "val/confusion_matrix_normalized.png", "val/BoxPR_curve.png", "val/BoxF1_curve.png",
    "val/val_batch0_labels.jpg", "val/val_batch0_pred.jpg", "val/val_batch1_pred.jpg", "test/confusion_matrix.png", "misc.png",
    "train/val_batch0_labels.jpg", "train/val_batch0_pred.jpg"
  ];
  const sections = groupResultImages(names.map((name) => ({ name })));
  const byKey = Object.fromEntries(sections.map((section) => [section.key, section]));
  assert.deepEqual(byKey.predictions.pairs, [
    { pred: "val/val_batch0_pred.jpg", labels: "val/val_batch0_labels.jpg", title: "val_batch0" },
    { pred: "val/val_batch1_pred.jpg", labels: null, title: "val_batch1" }
  ]);
  assert.deepEqual(byKey.curves.items, ["train/results.png"]);
  assert.deepEqual(byKey.confusion.items, ["val/confusion_matrix_normalized.png", "val/confusion_matrix.png", "test/confusion_matrix.png"]);
  assert.deepEqual(byKey.metricCurves.items, ["val/BoxPR_curve.png", "val/BoxF1_curve.png"]);
  assert.deepEqual(byKey.other.items, ["misc.png"]);
  assert.deepEqual(byKey.trainValPredictions.pairs, [{ pred: "train/val_batch0_pred.jpg", labels: "train/val_batch0_labels.jpg", title: "val_batch0" }]);
  assert.equal(sections.some((section) => section.key === "testPredictions"), false);
  assert.equal(describeArtifact("base/yolo26n.pt"), "基础权重 yolo26n.pt");
});
