// Undo/redo over whole-annotation-list snapshots. `record` is called after each edit with
// the new list; consecutive identical snapshots (e.g. a click without movement) are ignored.
function clone(value) {
  return JSON.parse(JSON.stringify(value ?? []));
}

export function createAnnotationHistory(limit = 100) {
  let undoStack = [];
  let redoStack = [];
  let current = null;
  let currentKey = "";

  function setCurrent(snapshot) {
    current = clone(snapshot);
    currentKey = JSON.stringify(current);
  }

  return {
    reset(snapshot) {
      undoStack = [];
      redoStack = [];
      setCurrent(snapshot);
    },
    record(snapshot) {
      const key = JSON.stringify(snapshot ?? []);
      if (current !== null && key === currentKey) return false;
      if (current !== null) {
        undoStack.push(current);
        if (undoStack.length > limit) undoStack.shift();
      }
      setCurrent(snapshot);
      redoStack = [];
      return true;
    },
    undo() {
      if (!undoStack.length) return null;
      redoStack.push(current);
      setCurrent(undoStack.pop());
      return clone(current);
    },
    redo() {
      if (!redoStack.length) return null;
      undoStack.push(current);
      setCurrent(redoStack.pop());
      return clone(current);
    },
    get canUndo() {
      return undoStack.length > 0;
    },
    get canRedo() {
      return redoStack.length > 0;
    }
  };
}
