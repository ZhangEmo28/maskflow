import test from "node:test";
import assert from "node:assert/strict";
import {
  boxFromSegment,
  insertVertex,
  moveBox,
  moveVertex,
  nearestEdge,
  nudgeBox,
  removeVertex,
  resizeBox,
  scrollForAnchor,
  transformSegment,
  zoomAnchor
} from "../src/lib/boxEditing.js";
import { createAnnotationHistory } from "../src/lib/annotationHistory.js";
import { isTypingTarget, resolveShortcut } from "../src/lib/shortcuts.js";

const close = (actual, expected, message) => assert.ok(Math.abs(actual - expected) < 1e-9, `${message}: ${actual} != ${expected}`);
const closeBox = (actual, expected) => {
  for (const key of ["cx", "cy", "width", "height"]) close(actual[key], expected[key], key);
};

test("moving a box stops at the image border without shrinking it", () => {
  const box = { cx: 0.5, cy: 0.5, width: 0.2, height: 0.2 };
  closeBox(moveBox(box, 0.1, -0.05), { cx: 0.6, cy: 0.45, width: 0.2, height: 0.2 });
  closeBox(moveBox(box, 0.9, 0.9), { cx: 0.9, cy: 0.9, width: 0.2, height: 0.2 });
  closeBox(nudgeBox(box, 10, 0, 100, 100), { cx: 0.6, cy: 0.5, width: 0.2, height: 0.2 });
});

test("resizing from a corner moves only that corner and never inverts", () => {
  const box = { cx: 0.5, cy: 0.5, width: 0.2, height: 0.2 };
  closeBox(resizeBox(box, "se", { x: 0.8, y: 0.7 }), { cx: 0.6, cy: 0.55, width: 0.4, height: 0.3 });
  closeBox(resizeBox(box, "n", { x: 0.1, y: 0.3 }), { cx: 0.5, cy: 0.45, width: 0.2, height: 0.3 });
  const collapsed = resizeBox(box, "w", { x: 0.95, y: 0.5 });
  assert.ok(collapsed.width > 0 && collapsed.cx + collapsed.width / 2 <= 0.6 + 1e-9);
});

test("polygons follow their box and the box follows edited polygons", () => {
  const from = { cx: 0.5, cy: 0.5, width: 0.2, height: 0.2 };
  const to = { cx: 0.6, cy: 0.5, width: 0.4, height: 0.2 };
  const square = [0.4, 0.4, 0.6, 0.4, 0.6, 0.6, 0.4, 0.6];
  const moved = transformSegment(square, from, to);
  [0.4, 0.4, 0.8, 0.4, 0.8, 0.6, 0.4, 0.6].forEach((value, index) => close(moved[index], value, `point ${index}`));
  closeBox(boxFromSegment(moveVertex(square, 2, { x: 0.7, y: 0.9 })), { cx: 0.55, cy: 0.65, width: 0.3, height: 0.5 });
});

test("vertices can be inserted on the nearest edge and removed down to a triangle", () => {
  const square = [0, 0, 1, 0, 1, 1, 0, 1];
  const edge = nearestEdge(square, { x: 0.5, y: 0.02 });
  assert.equal(edge.index, 0);
  const inserted = insertVertex(square, edge.index, { x: 0.5, y: 0 });
  assert.deepEqual(inserted, [0, 0, 0.5, 0, 1, 0, 1, 1, 0, 1]);
  const triangle = removeVertex(square, 3);
  assert.deepEqual(triangle, [0, 0, 1, 0, 1, 1]);
  assert.deepEqual(removeVertex(triangle, 0), triangle);
});

test("zooming keeps the image point under the cursor in place", () => {
  // 1000x500 frame at padding 18, scrolled by (100, 50), cursor at (300, 200) inside the viewport.
  const anchor = zoomAnchor({ cursorX: 300, cursorY: 200, scrollLeft: 100, scrollTop: 50, frameLeft: 18, frameTop: 18, frameWidth: 1000, frameHeight: 500 });
  close(anchor.x, 0.382, "anchor x");
  close(anchor.y, 0.464, "anchor y");
  // Zoomed to 2000x1000: the same image point must land under the cursor again.
  const next = scrollForAnchor({ anchor, cursorX: 300, cursorY: 200, frameLeft: 18, frameTop: 18, frameWidth: 2000, frameHeight: 1000 });
  close(next.scrollLeft, 18 + 764 - 300, "scroll x");
  close(next.scrollTop, 18 + 464 - 200, "scroll y");
});

test("history undoes and redoes snapshots and ignores no-op edits", () => {
  const history = createAnnotationHistory();
  history.reset([{ id: "a" }]);
  assert.equal(history.record([{ id: "a" }]), false);
  history.record([{ id: "a" }, { id: "b" }]);
  history.record([{ id: "b" }]);
  assert.deepEqual(history.undo(), [{ id: "a" }, { id: "b" }]);
  assert.deepEqual(history.undo(), [{ id: "a" }]);
  assert.equal(history.undo(), null);
  assert.deepEqual(history.redo(), [{ id: "a" }, { id: "b" }]);
  history.record([{ id: "c" }]);
  assert.equal(history.canRedo, false);
});

test("history copies snapshots so later mutation cannot rewrite the past", () => {
  const history = createAnnotationHistory();
  const items = [{ id: "a", bbox: { cx: 0.5 } }];
  history.reset(items);
  items[0].bbox.cx = 0.9;
  history.record(items);
  assert.equal(history.undo()[0].bbox.cx, 0.5);
});

test("shortcuts map by physical key, respect inputs and leave browser combos alone", () => {
  const body = { tagName: "BODY" };
  assert.deepEqual(resolveShortcut({ code: "Digit3", key: "3", target: body }), { action: "label", index: 2 });
  assert.deepEqual(resolveShortcut({ code: "KeyD", key: "d", target: body }), { action: "nextImage" });
  assert.deepEqual(resolveShortcut({ code: "KeyZ", key: "z", metaKey: true, target: body }), { action: "undo" });
  assert.deepEqual(resolveShortcut({ code: "KeyZ", key: "Z", ctrlKey: true, shiftKey: true, target: body }), { action: "redo" });
  assert.deepEqual(resolveShortcut({ code: "ArrowLeft", key: "ArrowLeft", shiftKey: true, target: body }), { action: "nudge", dx: -10, dy: 0 });
  assert.deepEqual(resolveShortcut({ code: "BracketRight", key: "】", target: body }), { action: "nextAnnotation" });
  assert.equal(resolveShortcut({ code: "Tab", key: "Tab", target: body }), null);
  assert.equal(resolveShortcut({ code: "KeyC", key: "c", ctrlKey: true, target: body }), null);
  assert.equal(resolveShortcut({ code: "KeyD", key: "d", target: { tagName: "INPUT", type: "text" } }), null);
  assert.deepEqual(resolveShortcut({ code: "Escape", key: "Escape", target: { tagName: "INPUT", type: "text" } }), { action: "escape" });
  assert.deepEqual(resolveShortcut({ code: "KeyD", key: "d", target: { tagName: "INPUT", type: "range" } }), { action: "nextImage" });
  assert.equal(resolveShortcut({ code: "ArrowLeft", key: "ArrowLeft", target: { tagName: "INPUT", type: "range" } }), null);
  assert.equal(resolveShortcut({ code: "KeyD", key: "Process", isComposing: true, target: body }), null);
  assert.equal(isTypingTarget({ tagName: "SELECT" }), true);
});
