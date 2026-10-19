"""MaskFlow training service.

Runs on the same image as the inference service but as its own container, so training never
blocks interactive annotation. Base weights are files an administrator placed in WEIGHTS_DIR
(trusted .pt checkpoints); each training run executes in a subprocess (app.train_runner) so it
can be cancelled and frees GPU memory when it ends.
"""
from __future__ import annotations

import csv
import hmac
import json
import math
import os
import re
import shutil
import signal
import subprocess
import sys
import threading
import zipfile
from pathlib import Path
from typing import Any

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, JSONResponse
from starlette.requests import Request

WEIGHTS_DIR = Path(os.getenv("TRAIN_WEIGHTS_DIR", "/weights"))
RUNS_DIR = Path(os.getenv("TRAIN_RUNS_DIR", "/runs"))
INTERNAL_KEY = os.getenv("SAM3_INTERNAL_KEY", "")
REQUIRE_INTERNAL_KEY = os.getenv("SAM3_REQUIRE_INTERNAL_KEY", "false").lower() in {"1", "true", "yes", "on"}
MAX_DATASET_BYTES = int(os.getenv("TRAIN_MAX_DATASET_BYTES", str(8 * 1024 * 1024 * 1024)))
MAX_DATASET_ENTRIES = 500_000
WEIGHT_SUFFIXES = {".pt"}
ARTIFACTS = {
    "best.pt": "train/weights/best.pt",
    "last.pt": "train/weights/last.pt",
    "best.onnx": "train/weights/best.onnx",
    "results.csv": "train/results.csv",
    "results.png": "train/results.png",
    "confusion_matrix.png": "val/confusion_matrix.png",
    "confusion_matrix_normalized.png": "val/confusion_matrix_normalized.png",
    "metrics.json": "metrics.json",
}
_RUN_ID = re.compile(r"^[a-z0-9_]{1,64}$")

if REQUIRE_INTERNAL_KEY and not INTERNAL_KEY:
    raise RuntimeError("SAM3_INTERNAL_KEY is required when SAM3_REQUIRE_INTERNAL_KEY is enabled.")

app = FastAPI(title="MaskFlow Trainer")
_processes: dict[str, subprocess.Popen] = {}
_lock = threading.Lock()


@app.middleware("http")
async def verify_internal_key(request: Request, call_next):
    if request.url.path.startswith("/api/") and (REQUIRE_INTERNAL_KEY or INTERNAL_KEY):
        if not hmac.compare_digest(request.headers.get("X-Internal-Key", ""), INTERNAL_KEY):
            return JSONResponse(status_code=401, content={"detail": "Unauthorized"})
    return await call_next(request)


@app.get("/health")
def health() -> dict[str, bool]:
    return {"ok": True}


# ---- Base weights ----

def infer_task(name: str) -> str:
    lower = name.lower()
    for suffix, task in (("-seg", "segment"), ("-cls", "classify"), ("-pose", "pose"), ("-obb", "obb")):
        if suffix in lower:
            return task
    return "detect"


def list_weights() -> list[dict[str, Any]]:
    if not WEIGHTS_DIR.exists():
        return []
    return [
        {"name": path.name, "size": path.stat().st_size, "task": infer_task(path.name)}
        for path in sorted(WEIGHTS_DIR.iterdir())
        if path.is_file() and path.suffix.lower() in WEIGHT_SUFFIXES
    ]


def weight_path(name: str) -> Path:
    # Only plain file names that exist directly in the weights directory: no paths, no traversal.
    if not name or Path(name).name != name or name not in {item["name"] for item in list_weights()}:
        raise HTTPException(status_code=404, detail=f"基础权重不存在：{name}。请把 .pt 文件放到训练权重目录。")
    return WEIGHTS_DIR / name


@app.get("/api/weights")
def weights() -> dict[str, Any]:
    return {"weights": list_weights(), "directory": str(WEIGHTS_DIR)}


# ---- Runs ----

def run_dir(run_id: str) -> Path:
    if not _RUN_ID.match(run_id or ""):
        raise HTTPException(status_code=400, detail="Invalid run id.")
    return RUNS_DIR / run_id


def extract_dataset(archive: Path, target: Path) -> Path:
    target.mkdir(parents=True, exist_ok=True)
    root = target.resolve()
    total = 0
    with zipfile.ZipFile(archive) as zf:
        entries = zf.infolist()
        if len(entries) > MAX_DATASET_ENTRIES:
            raise HTTPException(status_code=413, detail="Dataset has too many files.")
        for entry in entries:
            destination = (target / entry.filename).resolve()
            if not str(destination).startswith(str(root) + os.sep) and destination != root:
                raise HTTPException(status_code=400, detail=f"Unsafe path in dataset: {entry.filename}")
            total += entry.file_size
            if total > MAX_DATASET_BYTES:
                raise HTTPException(status_code=413, detail="Dataset is too large.")
        zf.extractall(target)

    # Classification crops are an ImageNet-style folder tree; Ultralytics takes the directory itself.
    classification = target / "classification"
    if (classification / "train").is_dir():
        return classification

    data_yaml = target / "data.yaml"
    if not data_yaml.exists():
        raise HTTPException(status_code=400, detail="Dataset zip has no data.yaml.")
    # MaskFlow exports use `path: .`; point it at the extracted directory.
    text = data_yaml.read_text(encoding="utf-8")
    text = re.sub(r"(?m)^path:.*$", f"path: {target.resolve().as_posix()}", text)
    data_yaml.write_text(text, encoding="utf-8")
    return data_yaml


def _launch(directory: Path) -> subprocess.Popen:
    with open(directory / "train.log", "ab") as log:
        return subprocess.Popen(
            [sys.executable, "-m", "app.train_runner", str(directory / "config.json")],
            stdout=log,
            stderr=subprocess.STDOUT,
            cwd=str(Path(__file__).resolve().parents[1]),
            start_new_session=True,
        )


def _running_run() -> str | None:
    for run_id, process in list(_processes.items()):
        if process.poll() is None:
            return run_id
        _processes.pop(run_id, None)
    return None


@app.get("/api/status")
def trainer_status() -> dict[str, Any]:
    with _lock:
        return {"running": _running_run()}


@app.post("/api/train")
async def start_training(
    dataset: UploadFile = File(...),
    run_id: str = Form(...),
    base_weight: str = Form(...),
    epochs: int = Form(50),
    imgsz: int = Form(640),
    batch: int = Form(16),
    patience: int = Form(20),
    device: str = Form(""),
) -> dict[str, Any]:
    directory = run_dir(run_id)
    weights_file = weight_path(base_weight)
    if not (1 <= epochs <= 1000 and 32 <= imgsz <= 4096 and -1 <= batch <= 512 and 0 <= patience <= 1000):
        raise HTTPException(status_code=400, detail="训练参数超出范围。")
    with _lock:
        busy = _running_run()
        if busy:
            raise HTTPException(status_code=409, detail=f"训练服务正忙（{busy}），请稍后。")
        if directory.exists():
            shutil.rmtree(directory)
        directory.mkdir(parents=True)

    archive = directory / "dataset.zip"
    with archive.open("wb") as handle:
        while chunk := await dataset.read(1024 * 1024):
            handle.write(chunk)
    try:
        data_yaml = extract_dataset(archive, directory / "data")
    except Exception:
        shutil.rmtree(directory, ignore_errors=True)
        raise
    finally:
        archive.unlink(missing_ok=True)

    config = {
        "run_dir": str(directory),
        "data": str(data_yaml),
        "weights": str(weights_file),
        "epochs": epochs,
        "imgsz": imgsz,
        "batch": batch,
        "patience": patience,
        "device": device or None,
    }
    (directory / "config.json").write_text(json.dumps(config), encoding="utf-8")
    write_status(directory, "queued")
    with _lock:
        _processes[run_id] = _launch(directory)
    return {"runId": run_id, "status": "running"}


def write_status(directory: Path, phase: str, **extra: Any) -> None:
    (directory / "status.json").write_text(json.dumps({"phase": phase, **extra}), encoding="utf-8")


def read_history(directory: Path) -> list[dict[str, float]]:
    results = directory / "train" / "results.csv"
    if not results.exists():
        return []
    rows: list[dict[str, float]] = []
    with results.open(encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            parsed = {}
            for key, value in row.items():
                try:
                    number = float(value)
                except (TypeError, ValueError):
                    continue
                if math.isfinite(number):  # early epochs can log NaN losses; JSON has no NaN
                    parsed[(key or "").strip()] = number
            if parsed:
                rows.append(parsed)
    return rows


def log_tail(directory: Path, lines: int = 20) -> str:
    log = directory / "train.log"
    if not log.exists():
        return ""
    return "\n".join(log.read_text(encoding="utf-8", errors="replace").splitlines()[-lines:])


@app.get("/api/train/{run_id}")
def training_status(run_id: str) -> dict[str, Any]:
    directory = run_dir(run_id)
    if not directory.exists():
        raise HTTPException(status_code=404, detail="Run not found.")
    status = json.loads((directory / "status.json").read_text(encoding="utf-8")) if (directory / "status.json").exists() else {"phase": "queued"}
    process = _processes.get(run_id)
    phase = status.get("phase", "queued")
    # A runner that died without reporting is a failure (crash, OOM kill, container restart).
    if phase not in {"completed", "failed", "cancelled"} and (process is None or process.poll() is not None):
        phase = "failed"
        status["error"] = status.get("error") or "训练进程意外退出：\n" + log_tail(directory)
        write_status(directory, phase, error=status["error"])
    config = json.loads((directory / "config.json").read_text(encoding="utf-8")) if (directory / "config.json").exists() else {}
    metrics = json.loads((directory / "metrics.json").read_text(encoding="utf-8")) if (directory / "metrics.json").exists() else None
    history = read_history(directory)
    return {
        "runId": run_id,
        "phase": phase,
        "epoch": len(history),
        "epochs": config.get("epochs"),
        "history": history,
        "metrics": metrics,
        "error": status.get("error"),
        "artifacts": [name for name, relative in ARTIFACTS.items() if (directory / relative).exists()],
    }


@app.post("/api/train/{run_id}/cancel")
def cancel_training(run_id: str) -> dict[str, Any]:
    directory = run_dir(run_id)
    process = _processes.get(run_id)
    if process and process.poll() is None:
        try:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=30)
        except (ProcessLookupError, subprocess.TimeoutExpired):
            os.killpg(process.pid, signal.SIGKILL)
    if directory.exists():
        write_status(directory, "cancelled")
    return {"runId": run_id, "phase": "cancelled"}


@app.get("/api/train/{run_id}/artifacts/{name}")
def artifact(run_id: str, name: str):
    relative = ARTIFACTS.get(name)
    if relative is None:
        raise HTTPException(status_code=404, detail="Unknown artifact.")
    path = run_dir(run_id) / relative
    if not path.exists():
        raise HTTPException(status_code=404, detail="Artifact not available.")
    return FileResponse(path, filename=name)


@app.delete("/api/train/{run_id}")
def delete_run(run_id: str) -> dict[str, bool]:
    directory = run_dir(run_id)
    process = _processes.get(run_id)
    if process and process.poll() is None:
        raise HTTPException(status_code=409, detail="Run is still training.")
    shutil.rmtree(directory, ignore_errors=True)
    _processes.pop(run_id, None)
    return {"deleted": True}
