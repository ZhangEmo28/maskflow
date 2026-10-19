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
