"""MaskFlow training service.

Runs on the same image as the inference service but as its own container, so training never
blocks interactive annotation. Base weights live in WEIGHTS_DIR: official Ultralytics checkpoints
downloaded from a fixed catalog (size-checked), or trusted .pt files an administrator placed
there. Each training run executes in a subprocess (app.train_runner) so it can be cancelled and
frees GPU memory when it ends.
"""
from __future__ import annotations

import csv
import hashlib
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
import urllib.request
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
# Internal files of a run that are not training results (the extracted dataset is kept by the API).
_PRIVATE_RUN_ENTRIES = {"data", "config.json", "status.json", "dataset.zip"}

# Official checkpoints (github.com/ultralytics/assets, release v8.4.0) with their exact sizes, which
# double as an integrity check. TRAIN_WEIGHT_BASE_URL can point at a mirror of that release.
WEIGHT_BASE_URL = os.getenv("TRAIN_WEIGHT_BASE_URL", "https://github.com/ultralytics/assets/releases/download/v8.4.0").rstrip("/")
_CATALOG_SIZES = {
    "yolo26n.pt": 5544453, "yolo26s.pt": 20422725, "yolo26m.pt": 44255705, "yolo26l.pt": 53211173, "yolo26x.pt": 118667365,
    "yolo26n-seg.pt": 6719965, "yolo26s-seg.pt": 23467933, "yolo26m-seg.pt": 54750385, "yolo26l-seg.pt": 63700037, "yolo26x-seg.pt": 142129861,
    "yolo26n-cls.pt": 5786434, "yolo26s-cls.pt": 13622082, "yolo26m-cls.pt": 23477398, "yolo26l-cls.pt": 28535928, "yolo26x-cls.pt": 59610168,
    "yolo11n.pt": 5613764, "yolo11s.pt": 19313732, "yolo11m.pt": 40684120, "yolo11l.pt": 51387343, "yolo11x.pt": 114636239,
    "yolo11n-seg.pt": 6182636, "yolo11s-seg.pt": 20669228, "yolo11m-seg.pt": 45400152, "yolo11l-seg.pt": 56096965, "yolo11x-seg.pt": 125090821,
    "yolo11n-cls.pt": 5790624, "yolo11s-cls.pt": 13630112, "yolo11m-cls.pt": 23487128, "yolo11l-cls.pt": 28553700, "yolo11x-cls.pt": 59624868,
    "yolov8n.pt": 6549796, "yolov8s.pt": 22588772, "yolov8m.pt": 52136884, "yolov8l.pt": 87792836, "yolov8x.pt": 136890692,
    "yolov8n-seg.pt": 7071756, "yolov8s-seg.pt": 23914764, "yolov8m-seg.pt": 54921020, "yolov8l-seg.pt": 92417004, "yolov8x-seg.pt": 144101612,
    "yolov8n-cls.pt": 5563076, "yolov8s-cls.pt": 12856196, "yolov8m-cls.pt": 34285204, "yolov8l-cls.pt": 75193188, "yolov8x-cls.pt": 115090020,
}
_FAMILIES = {"yolo26": "YOLO26", "yolo11": "YOLO11", "yolov8": "YOLOv8"}
_SCALES = {"n": "nano · 最快", "s": "small", "m": "medium", "l": "large", "x": "xlarge · 最准"}
_downloads: dict[str, dict[str, Any]] = {}

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


def weight_catalog() -> list[dict[str, Any]]:
    entries = []
    for name, size in _CATALOG_SIZES.items():
        match = re.fullmatch(r"(yolo26|yolo11|yolov8)([nsmlx])(-seg|-cls)?\.pt", name)
        family, scale = match.group(1), match.group(2)
        entries.append({
            "name": name, "family": _FAMILIES[family], "scale": scale, "scaleLabel": _SCALES[scale],
            "task": infer_task(name), "size": size, "url": f"{WEIGHT_BASE_URL}/{name}",
        })
    return entries


def _weight_in_use() -> str | None:
    running = _running_run()
    if running is None:
        return None
    config = RUNS_DIR / running / "config.json"
    try:
        return json.loads(config.read_text(encoding="utf-8")).get("base_weight")
    except (OSError, ValueError):
        return None


@app.get("/api/weights")
def weights() -> dict[str, Any]:
    installed = {item["name"]: item for item in list_weights()}
    catalog = []
    for entry in weight_catalog():
        state = _downloads.get(entry["name"])
        catalog.append({
            **entry,
            "installed": entry["name"] in installed,
            "download": None if state is None or (state["done"] and not state["error"]) else dict(state),
        })
    writable = WEIGHTS_DIR.exists() and os.access(WEIGHTS_DIR, os.W_OK)
    return {"weights": list(installed.values()), "catalog": catalog, "directory": str(WEIGHTS_DIR), "writable": writable, "inUse": _weight_in_use()}


def _open_url(url: str):
    return urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "MaskFlow-Trainer"}), timeout=60)


def _download_weight(entry: dict[str, Any], state: dict[str, Any]) -> None:
    target = WEIGHTS_DIR / entry["name"]
    partial = WEIGHTS_DIR / (entry["name"] + ".part")
    try:
        with _open_url(entry["url"]) as response, partial.open("wb") as handle:
            while chunk := response.read(1024 * 1024):
                state["received"] += len(chunk)
                if state["received"] > entry["size"]:
                    raise ValueError("下载的文件比官方文件大，已中止。")
                handle.write(chunk)
        # Exact size and a PyTorch checkpoint container (zip) guard against truncated or wrong files.
        if state["received"] != entry["size"]:
            raise ValueError(f"文件不完整：{state['received']} / {entry['size']} 字节。")
        if not zipfile.is_zipfile(partial):
            raise ValueError("下载的文件不是有效的 PyTorch 权重。")
        partial.replace(target)
    except Exception as exc:  # reported through GET /api/weights
        state["error"] = f"{type(exc).__name__}: {exc}" if not isinstance(exc, ValueError) else str(exc)
        partial.unlink(missing_ok=True)
    finally:
        state["done"] = True


@app.post("/api/weights/{name}/download")
def download_weight(name: str) -> dict[str, Any]:
    entry = next((item for item in weight_catalog() if item["name"] == name), None)
    if entry is None:
        raise HTTPException(status_code=404, detail="只能下载目录中列出的官方权重。")
    if (WEIGHTS_DIR / name).exists():
        return {"name": name, "installed": True}
    if not WEIGHTS_DIR.exists() or not os.access(WEIGHTS_DIR, os.W_OK):
        raise HTTPException(status_code=409, detail=f"权重目录 {WEIGHTS_DIR} 不可写，请检查挂载（不能是只读）。")
    with _lock:
        state = _downloads.get(name)
        if state and not state["done"]:
            return {"name": name, "download": dict(state)}
        state = {"received": 0, "total": entry["size"], "error": None, "done": False}
        _downloads[name] = state
    threading.Thread(target=_download_weight, args=(entry, state), daemon=True).start()
    return {"name": name, "download": dict(state)}


@app.delete("/api/weights/{name}")
def delete_weight(name: str) -> dict[str, bool]:
    path = weight_path(name)
    if _weight_in_use() == name:
        raise HTTPException(status_code=409, detail="这个权重正在被训练使用，训练结束后再删除。")
    path.unlink(missing_ok=True)
    _downloads.pop(name, None)
    return {"deleted": True}


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

    base_dir = directory / "base"
    base_dir.mkdir()
    base_copy = base_dir / weights_file.name
    shutil.copy2(weights_file, base_copy)
    config = {
        "run_dir": str(directory),
        "data": str(data_yaml),
        "weights": str(base_copy),
        "base_weight": weights_file.name,
        "base_weight_sha256": _sha256(base_copy),
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


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


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


def run_files(directory: Path) -> list[dict[str, Any]]:
    files = []
    for path in sorted(directory.rglob("*")):
        relative = path.relative_to(directory)
        if not path.is_file() or relative.parts[0] in _PRIVATE_RUN_ENTRIES or path.name.endswith(".part"):
            continue
        files.append({"path": relative.as_posix(), "size": path.stat().st_size})
    return files


# Everything a run produced (base weight copy, weights, plots, csv, args, logs, metrics), for the
# API to keep and package; the extracted dataset and internal state files are excluded.
@app.get("/api/train/{run_id}/files")
def training_files(run_id: str) -> dict[str, Any]:
    directory = run_dir(run_id)
    if not directory.exists():
        raise HTTPException(status_code=404, detail="Run not found.")
    return {"files": run_files(directory)}


@app.get("/api/train/{run_id}/files/{path:path}")
def training_file(run_id: str, path: str):
    directory = run_dir(run_id).resolve()
    candidate = (directory / path).resolve()
    if (not str(candidate).startswith(str(directory) + os.sep) or not candidate.is_file()
            or candidate.relative_to(directory).parts[0] in _PRIVATE_RUN_ENTRIES):
        raise HTTPException(status_code=404, detail="File not found.")
    return FileResponse(candidate, filename=candidate.name)


@app.delete("/api/train/{run_id}")
def delete_run(run_id: str) -> dict[str, bool]:
    directory = run_dir(run_id)
    process = _processes.get(run_id)
    if process and process.poll() is None:
        raise HTTPException(status_code=409, detail="Run is still training.")
    shutil.rmtree(directory, ignore_errors=True)
    _processes.pop(run_id, None)
    return {"deleted": True}
