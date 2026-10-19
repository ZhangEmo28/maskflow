// Keyboard shortcuts for the annotation workbench. Letter keys use `event.code` so they work
// with any keyboard layout and while a Chinese IME is active.

const NON_TYPING_INPUTS = new Set(["range", "checkbox", "radio", "button", "submit", "reset", "color", "file"]);

export function isTypingTarget(target) {
  if (!target) return false;
  if (target.isContentEditable) return true;
  const tag = String(target.tagName || "").toUpperCase();
  if (tag === "TEXTAREA" || tag === "SELECT") return true;
  if (tag !== "INPUT") return false;
  return !NON_TYPING_INPUTS.has(String(target.type || "text").toLowerCase());
}

export const SHORTCUT_HELP = [
  ["1 – 9", "给选中目标设置第 N 个标签"],
  ["A / D", "上一张 / 下一张图片"],
  ["] / [", "选中下一个 / 上一个目标"],
  ["方向键", "微调选中框 1 像素（Shift 为 10 像素）"],
  ["Delete / Backspace", "删除选中目标"],
  ["C", "切换选中目标的人工确认"],
  ["B", "手动画框模式"],
  ["P", "点提示模式"],
  ["Enter", "点提示模式下添加当前目标"],
  ["Esc", "退出当前模式 / 取消选中"],
  ["Ctrl/⌘ + S", "保存"],
  ["Ctrl/⌘ + Z", "撤销"],
  ["Ctrl/⌘ + Shift + Z 或 Ctrl + Y", "重做"],
  ["滚轮", "以光标为中心缩放"],
  ["空格 + 拖动 / 中键拖动", "平移画布"],
  ["F", "适配窗口"],
  ["+ / -", "放大 / 缩小"],
  ["双击多边形边", "插入顶点"],
  ["Alt + 点击顶点", "删除顶点"],
  ["?", "显示 / 隐藏快捷键说明"]
];

export function resolveShortcut(event) {
  if (!event || event.isComposing) return null;
  const key = event.key;
  const code = event.code || "";
  const mod = event.ctrlKey || event.metaKey;

  if (mod && !event.altKey) {
    if (code === "KeyS") return { action: "save" };
    if (code === "KeyZ") return { action: event.shiftKey ? "redo" : "undo" };
    if (code === "KeyY") return { action: "redo" };
    return null;
  }
  if (mod || event.altKey) return null;

  const typing = isTypingTarget(event.target);
  if (typing) return key === "Escape" ? { action: "escape" } : null;

  const tag = String(event.target?.tagName || "").toUpperCase();
  const isRangeInput = tag === "INPUT" && String(event.target?.type).toLowerCase() === "range";

  const digit = /^Digit([1-9])$/.exec(code) || /^Numpad([1-9])$/.exec(code);
  if (digit) return { action: "label", index: Number(digit[1]) - 1 };

  switch (code) {
    case "KeyA": return { action: "previousImage" };
    case "KeyD": return { action: "nextImage" };
    case "KeyC": return { action: "toggleConfirm" };
    case "KeyB": return { action: "drawMode" };
    case "KeyP": return { action: "pointMode" };
    case "KeyF": return { action: "fit" };
    case "BracketRight": return { action: "nextAnnotation" };
    case "BracketLeft": return { action: "previousAnnotation" };
    default: break;
  }

  switch (key) {
    case "Delete":
    case "Backspace":
      return { action: "delete" };
    case "Escape":
      return { action: "escape" };
    case "Enter":
      return { action: "enter" };
    case "+":
    case "=":
      return { action: "zoomIn" };
    case "-":
    case "_":
      return { action: "zoomOut" };
    case "?":
      return { action: "help" };
    default:
      break;
  }

  if (!isRangeInput && key.startsWith("Arrow")) {
    const step = event.shiftKey ? 10 : 1;
    const delta = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[key];
    if (delta) return { action: "nudge", dx: delta[0], dy: delta[1] };
  }
  return null;
}
