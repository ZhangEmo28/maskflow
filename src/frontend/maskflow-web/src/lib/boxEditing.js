// Geometry helpers for editing YOLO boxes ({ cx, cy, width, height }, normalized 0–1)
// and polygons (flat [x0, y0, x1, y1, ...], normalized 0–1).

export const MIN_BOX_SIZE = 0.002;
export const HANDLES = ["nw", "n", "ne", "e", "se", "s", "sw", "w"];

export function clamp01(value) {
  return Math.max(0, Math.min(1, Number(value) || 0));
}

export function boxEdges(box) {
  return {
    left: box.cx - box.width / 2,
    top: box.cy - box.height / 2,
    right: box.cx + box.width / 2,
    bottom: box.cy + box.height / 2
  };
}

export function boxFromEdges(left, top, right, bottom) {
  const l = clamp01(Math.min(left, right));
  const r = clamp01(Math.max(left, right));
  const t = clamp01(Math.min(top, bottom));
  const b = clamp01(Math.max(top, bottom));
  return { cx: (l + r) / 2, cy: (t + b) / 2, width: r - l, height: b - t };
}

// Moves without resizing: the box stops at the image border instead of shrinking.
export function moveBox(box, dx, dy) {
  const { left, top, right, bottom } = boxEdges(box);
  const clampedDx = Math.max(-left, Math.min(1 - right, dx));
  const clampedDy = Math.max(-top, Math.min(1 - bottom, dy));
  return { ...box, cx: box.cx + clampedDx, cy: box.cy + clampedDy };
}

export function resizeBox(box, handle, point) {
  let { left, top, right, bottom } = boxEdges(box);
  const x = clamp01(point.x);
  const y = clamp01(point.y);
  if (handle.includes("w")) left = Math.min(x, right - MIN_BOX_SIZE);
  if (handle.includes("e")) right = Math.max(x, left + MIN_BOX_SIZE);
  if (handle.includes("n")) top = Math.min(y, bottom - MIN_BOX_SIZE);
  if (handle.includes("s")) bottom = Math.max(y, top + MIN_BOX_SIZE);
  return boxFromEdges(left, top, right, bottom);
}

export function nudgeBox(box, dxPixels, dyPixels, imageWidth, imageHeight) {
  if (!(imageWidth > 0 && imageHeight > 0)) return box;
  return moveBox(box, dxPixels / imageWidth, dyPixels / imageHeight);
}

// Keeps a polygon attached to its box when the box is moved or resized.
export function transformSegment(segment, fromBox, toBox) {
  if (!Array.isArray(segment)) return segment;
  const from = boxEdges(fromBox);
  const to = boxEdges(toBox);
  const scaleX = fromBox.width > 0 ? toBox.width / fromBox.width : 1;
  const scaleY = fromBox.height > 0 ? toBox.height / fromBox.height : 1;
  return segment.map((value, index) => index % 2 === 0
    ? clamp01(to.left + (value - from.left) * scaleX)
    : clamp01(to.top + (value - from.top) * scaleY));
}

export function boxFromSegment(segment) {
  if (!Array.isArray(segment) || segment.length < 6) return null;
  const xs = segment.filter((_, index) => index % 2 === 0);
  const ys = segment.filter((_, index) => index % 2 === 1);
  return boxFromEdges(Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys));
}

export function moveVertex(segment, vertexIndex, point) {
  const next = [...segment];
  next[vertexIndex * 2] = clamp01(point.x);
  next[vertexIndex * 2 + 1] = clamp01(point.y);
  return next;
}

export function insertVertex(segment, afterVertexIndex, point) {
  const next = [...segment];
  next.splice((afterVertexIndex + 1) * 2, 0, clamp01(point.x), clamp01(point.y));
  return next;
}

// A polygon needs at least three vertices; removing below that is a no-op.
export function removeVertex(segment, vertexIndex) {
  if (segment.length <= 6) return [...segment];
  const next = [...segment];
  next.splice(vertexIndex * 2, 2);
  return next;
}

// Edge index i runs from vertex i to vertex i + 1 (wrapping). Distances use pixel aspect.
export function nearestEdge(segment, point, aspect = 1) {
  const count = segment.length / 2;
  let best = { index: -1, distance: Infinity };
  for (let i = 0; i < count; i += 1) {
    const j = (i + 1) % count;
    const ax = segment[i * 2] * aspect;
    const ay = segment[i * 2 + 1];
    const bx = segment[j * 2] * aspect;
    const by = segment[j * 2 + 1];
    const px = point.x * aspect;
    const py = point.y;
    const lengthSquared = (bx - ax) ** 2 + (by - ay) ** 2;
    const t = lengthSquared === 0 ? 0 : Math.max(0, Math.min(1, ((px - ax) * (bx - ax) + (py - ay) * (by - ay)) / lengthSquared));
    const distance = Math.hypot(px - (ax + t * (bx - ax)), py - (ay + t * (by - ay)));
    if (distance < best.distance) best = { index: i, distance };
  }
  return best;
}

export function handleCursor(handle) {
  return ({ n: "ns-resize", s: "ns-resize", e: "ew-resize", w: "ew-resize", ne: "nesw-resize", sw: "nesw-resize", nw: "nwse-resize", se: "nwse-resize" })[handle];
}

// Zooming around the cursor: `anchor` is the normalized image point under the cursor before
// zooming; after the frame is re-laid out, scroll so that point is under the cursor again.
// Works whether the frame is centered (smaller than the viewport) or scrolled.
export function zoomAnchor({ cursorX, cursorY, scrollLeft, scrollTop, frameLeft, frameTop, frameWidth, frameHeight }) {
  return {
    x: clamp01((cursorX + scrollLeft - frameLeft) / frameWidth),
    y: clamp01((cursorY + scrollTop - frameTop) / frameHeight)
  };
}

export function scrollForAnchor({ anchor, cursorX, cursorY, frameLeft, frameTop, frameWidth, frameHeight }) {
  return {
    scrollLeft: Math.max(0, frameLeft + anchor.x * frameWidth - cursorX),
    scrollTop: Math.max(0, frameTop + anchor.y * frameHeight - cursorY)
  };
}
