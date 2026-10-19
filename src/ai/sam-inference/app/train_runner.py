"""Subprocess entry point for one training run: train -> evaluate (val, and test if present) -> export ONNX.

Writes progress to status.json and the final evaluation to metrics.json in the run directory.
Usage: python -m app.train_runner /runs/<id>/config.json
"""
from __future__ import annotations

import json
import math
import sys
import traceback
from pathlib import Path
from typing import Any


def write_status(run_dir: Path, phase: str, **extra: Any) -> None:
    (run_dir / "status.json").write_text(json.dumps({"phase": phase, **extra}), encoding="utf-8")


def _finite(value: Any) -> float | None:
    number = float(value)
    return number if math.isfinite(number) else None


def _floats(values: Any) -> list[float | None]:
    try:
        return [_finite(value) for value in list(values)]
    except TypeError:
        return []


def summarize(result: Any, names: dict[int, str]) -> dict[str, Any]:
    """Overall metrics from Ultralytics' results_dict plus per-class mAP when available."""
    overall = {str(key): _finite(value) for key, value in (getattr(result, "results_dict", {}) or {}).items()}
    per_class: list[dict[str, Any]] = []
    for kind in ("seg", "box"):
        metric = getattr(result, kind, None)
        maps = _floats(getattr(metric, "maps", [])) if metric is not None else []
        indexes = _floats(getattr(metric, "ap_class_index", [])) if metric is not None else []
        ap50 = _floats(getattr(metric, "ap50", [])) if metric is not None else []
        if maps and indexes:
            for position, class_index in enumerate(int(value) for value in indexes if value is not None):
                per_class.append({
                    "class": names.get(class_index, str(class_index)),
                    "mAP50": ap50[position] if position < len(ap50) else None,
                    "mAP50-95": maps[class_index] if class_index < len(maps) else None,
                })
            break
    speed = {str(key): _finite(value) for key, value in (getattr(result, "speed", {}) or {}).items()}
    return {"overall": overall, "perClass": per_class, "speed": speed}


def has_test_split(data: Path) -> bool:
    # data is either data.yaml (detect/segment) or the classification root directory.
    test_dir = data / "test" if data.is_dir() else data.parent / "images" / "test"
    return test_dir.exists() and any(test_dir.iterdir())


def main(config_path: str) -> int:
    config = json.loads(Path(config_path).read_text(encoding="utf-8"))
    run_dir = Path(config["run_dir"])
    data = Path(config["data"])
    try:
        from ultralytics import YOLO

        write_status(run_dir, "training")
        model = YOLO(config["weights"])
        model.train(
            data=str(data),
            epochs=config["epochs"],
            imgsz=config["imgsz"],
            batch=config["batch"],
            patience=config["patience"],
            device=config.get("device"),
            project=str(run_dir),
            name="train",
            exist_ok=True,
            plots=True,
            verbose=False,
        )

        best = run_dir / "train" / "weights" / "best.pt"
        write_status(run_dir, "evaluating")
        trained = YOLO(str(best))
        names = dict(getattr(trained, "names", {}) or {})
        metrics: dict[str, Any] = {
            "baseWeight": Path(config["weights"]).name,
            "task": getattr(trained, "task", None),
            "names": [names[key] for key in sorted(names)],
            "val": summarize(trained.val(data=str(data), split="val", plots=True, project=str(run_dir), name="val", exist_ok=True), names),
        }
        if has_test_split(data):
            metrics["test"] = summarize(trained.val(data=str(data), split="test", plots=True, project=str(run_dir), name="test", exist_ok=True), names)

        write_status(run_dir, "exporting")
        exported = trained.export(format="onnx", imgsz=config["imgsz"])
        metrics["onnx"] = Path(str(exported)).name if exported else None
        (run_dir / "metrics.json").write_text(json.dumps(metrics, ensure_ascii=False), encoding="utf-8")
        write_status(run_dir, "completed")
        return 0
    except Exception as exc:  # reported to the API; the full trace stays in train.log
        traceback.print_exc()
        write_status(run_dir, "failed", error=f"{type(exc).__name__}: {exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
