"""Trainer tests with a stand-in `ultralytics` package (no torch, GPU or real weights needed).

The fake YOLO writes the same files Ultralytics does (results.csv, weights/best.pt, best.onnx),
so the real subprocess, status polling and artifact plumbing are exercised end to end.
"""
import io
import json
import os
import sys
import textwrap
import time
import zipfile
from pathlib import Path

import pytest

os.environ.setdefault("SAM3_INTERNAL_KEY", "test-internal-key")
os.environ.setdefault("SAM3_REQUIRE_INTERNAL_KEY", "true")
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fastapi.testclient import TestClient  # noqa: E402

from app import train_runner, trainer  # noqa: E402

HEADERS = {"X-Internal-Key": "test-internal-key"}
client = TestClient(trainer.app)

FAKE_ULTRALYTICS = textwrap.dedent(
    '''
    import json, os, time
    from pathlib import Path

    class _Metric:
        maps = [0.5, 0.25]
        ap_class_index = [0, 1]
        ap50 = [0.8, 0.6]

    class _Result:
        results_dict = {"metrics/precision(B)": 0.7, "metrics/mAP50(B)": 0.7, "metrics/mAP50-95(B)": 0.375, "fitness": 0.4}
        box = _Metric()
        speed = {"inference": 1.5}

    class YOLO:
        def __init__(self, path):
            self.path = Path(path)
            self.names = {0: "car", 1: "person"}
            self.task = "detect"

        def train(self, data, epochs, project, name, **kwargs):
            mode = os.environ.get("FAKE_YOLO_MODE", "ok")
            if mode == "fail":
                raise RuntimeError("CUDA out of memory")
            if mode == "crash":
                os._exit(9)
            out = Path(project) / name
            (out / "weights").mkdir(parents=True, exist_ok=True)
            (out / "fake_args.json").write_text(json.dumps({"weights": str(self.path), "data": data, "epochs": epochs, **kwargs}))
            with open(out / "results.csv", "w") as handle:
                handle.write("                  epoch,      train/box_loss,    metrics/mAP50(B)\\n")
                for epoch in range(1, epochs + 1):
                    handle.write(f"{epoch:>20},{1.0 / epoch:>20},{0.1 * epoch:>20}\\n")
                    handle.flush()
                    if mode == "sleep":
                        time.sleep(30)
            (out / "weights" / "best.pt").write_bytes(b"best")
            (out / "weights" / "last.pt").write_bytes(b"last")

        def val(self, data, split, project, name, **kwargs):
            out = Path(project) / name
            out.mkdir(parents=True, exist_ok=True)
            (out / "confusion_matrix.png").write_bytes(b"png")
            return _Result()

        def export(self, format, **kwargs):
            target = self.path.with_suffix("." + format)
            target.write_bytes(b"onnx")
            return str(target)
    '''
)


@pytest.fixture
def dirs(tmp_path, monkeypatch):
    weights = tmp_path / "weights"
    weights.mkdir()
    (weights / "yolo11n.pt").write_bytes(b"w" * 10)
    (weights / "yolo11s-seg.pt").write_bytes(b"w" * 20)
    (weights / "yolo11n-cls.pt").write_bytes(b"w")
    (weights / "notes.txt").write_text("not a weight")
    runs = tmp_path / "runs"
    fake = tmp_path / "fake"
    (fake / "ultralytics").mkdir(parents=True)
    (fake / "ultralytics" / "__init__.py").write_text(FAKE_ULTRALYTICS)
    monkeypatch.setattr(trainer, "WEIGHTS_DIR", weights)
    monkeypatch.setattr(trainer, "RUNS_DIR", runs)
    monkeypatch.setenv("PYTHONPATH", str(fake))
    monkeypatch.setattr(trainer, "_processes", {})
    yield {"weights": weights, "runs": runs, "fake": fake}
    for process in trainer._processes.values():
        if process.poll() is None:
            process.kill()


def dataset_zip(test_images=1, extra=None):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as zf:
        zf.writestr("data.yaml", "path: .\ntask: detect\ntrain: images/train\nval: images/val\ntest: images/test\nnc: 2\nnames:\n  0: \"car\"\n  1: \"person\"\n")
        zf.writestr("images/train/a.jpg", b"img")
        zf.writestr("labels/train/a.txt", "0 0.5 0.5 0.2 0.2\n")
        zf.writestr("images/val/b.jpg", b"img")
        for index in range(test_images):
            zf.writestr(f"images/test/t{index}.jpg", b"img")
        for name, content in (extra or {}).items():
            zf.writestr(name, content)
    return buffer.getvalue()


def start(run_id="run_1", weight="yolo11n.pt", data=None, **fields):
    form = {"run_id": run_id, "base_weight": weight, "epochs": "3", **{key: str(value) for key, value in fields.items()}}
    return client.post("/api/train", headers=HEADERS, data=form, files={"dataset": ("dataset.zip", data or dataset_zip(), "application/zip")})


def wait_for(run_id, phases=("completed", "failed", "cancelled"), timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        status = client.get(f"/api/train/{run_id}", headers=HEADERS).json()
        if status["phase"] in phases:
            return status
        time.sleep(0.1)
    raise AssertionError(f"run {run_id} did not reach {phases}: {status}")


def test_requires_internal_key(dirs):
    assert client.get("/api/weights").status_code == 401
    assert client.get("/health").status_code == 200


def test_lists_only_pt_weights_with_inferred_task(dirs):
    weights = client.get("/api/weights", headers=HEADERS).json()["weights"]
    assert [(item["name"], item["task"], item["size"]) for item in weights] == [
        ("yolo11n-cls.pt", "classify", 1),
        ("yolo11n.pt", "detect", 10),
        ("yolo11s-seg.pt", "segment", 20),
    ]


@pytest.mark.parametrize("weight", ["../yolo11n.pt", "missing.pt", "notes.txt", "/etc/passwd"])
def test_rejects_unknown_or_unsafe_weights(dirs, weight):
    assert start(weight=weight).status_code == 404
    assert not dirs["runs"].exists() or not any(dirs["runs"].iterdir())


@pytest.mark.parametrize("run_id", ["../x", "Run", "a/b", ""])
def test_rejects_bad_run_ids(dirs, run_id):
    assert start(run_id=run_id).status_code in {400, 422}


def test_rejects_zip_slip_and_missing_yaml(dirs):
    assert start(data=dataset_zip(extra={"../../evil.txt": "x"})).status_code == 400
    assert not (dirs["runs"].parent / "evil.txt").exists()
    assert not (dirs["runs"] / "run_1").exists()

    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as zf:
        zf.writestr("images/train/a.jpg", b"img")
    response = start(data=buffer.getvalue())
    assert response.status_code == 400
    assert "data.yaml" in response.json()["detail"]


def test_full_run_trains_evaluates_exports_and_serves_artifacts(dirs):
    response = start(epochs=3, imgsz=320, batch=4, patience=5)
    assert response.status_code == 200, response.text

    status = wait_for("run_1")
    assert status["phase"] == "completed", status
    assert status["epoch"] == 3 and status["epochs"] == 3
    assert status["history"][-1]["metrics/mAP50(B)"] == pytest.approx(0.3)
    assert status["history"][0]["epoch"] == 1
    metrics = status["metrics"]
    assert metrics["baseWeight"] == "yolo11n.pt"
    assert metrics["names"] == ["car", "person"]
    assert metrics["val"]["overall"]["metrics/mAP50-95(B)"] == 0.375
    assert metrics["val"]["perClass"] == [
        {"class": "car", "mAP50": 0.8, "mAP50-95": 0.5},
        {"class": "person", "mAP50": 0.6, "mAP50-95": 0.25},
    ]
    assert "test" in metrics and metrics["onnx"] == "best.onnx"
    assert set(status["artifacts"]) >= {"best.pt", "last.pt", "best.onnx", "results.csv", "metrics.json", "confusion_matrix.png"}

    args = json.loads((dirs["runs"] / "run_1" / "train" / "fake_args.json").read_text())
    data_yaml = Path(args["data"])
    assert args["weights"] == str(dirs["weights"] / "yolo11n.pt")
    assert (args["imgsz"], args["batch"], args["patience"], args["device"]) == (320, 4, 5, None)
    # `path: .` is rewritten to the extracted absolute directory so Ultralytics finds the images.
    assert f"path: {data_yaml.parent.resolve().as_posix()}" in data_yaml.read_text()

    onnx = client.get("/api/train/run_1/artifacts/best.onnx", headers=HEADERS)
    assert (onnx.status_code, onnx.content) == (200, b"onnx")
    assert client.get("/api/train/run_1/artifacts/config.json", headers=HEADERS).status_code == 404
    assert client.get("/api/train/run_1/artifacts/..%2Fconfig.json", headers=HEADERS).status_code == 404

    assert client.delete("/api/train/run_1", headers=HEADERS).json() == {"deleted": True}
    assert not (dirs["runs"] / "run_1").exists()


def test_skips_test_evaluation_without_test_images(dirs):
    assert start(data=dataset_zip(test_images=0)).status_code == 200
    status = wait_for("run_1")
    assert status["phase"] == "completed" and "test" not in status["metrics"]


def test_classification_dataset_passes_the_folder(dirs):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as zf:
        zf.writestr("classes.txt", "car\nperson\n")
        zf.writestr("classification/train/car/a.jpg", b"img")
        zf.writestr("classification/val/car/b.jpg", b"img")
    assert start(weight="yolo11n-cls.pt", data=buffer.getvalue()).status_code == 200
    assert wait_for("run_1")["phase"] == "completed"
    args = json.loads((dirs["runs"] / "run_1" / "train" / "fake_args.json").read_text())
    assert Path(args["data"]) == (dirs["runs"] / "run_1" / "data" / "classification")


def test_training_errors_are_reported(dirs, monkeypatch):
    monkeypatch.setenv("FAKE_YOLO_MODE", "fail")
    assert start().status_code == 200
    status = wait_for("run_1")
    assert status["phase"] == "failed"
    assert "CUDA out of memory" in status["error"]


def test_a_runner_that_dies_silently_is_marked_failed(dirs, monkeypatch):
    monkeypatch.setenv("FAKE_YOLO_MODE", "crash")
    assert start().status_code == 200
    status = wait_for("run_1")
    assert status["phase"] == "failed"
    assert "意外退出" in status["error"]


def test_one_run_at_a_time_and_cancel(dirs, monkeypatch):
    monkeypatch.setenv("FAKE_YOLO_MODE", "sleep")
    assert start().status_code == 200
    wait_for("run_1", phases=("training",))

    assert client.get("/api/status", headers=HEADERS).json() == {"running": "run_1"}
    busy = start(run_id="run_2")
    assert busy.status_code == 409
    assert client.delete("/api/train/run_1", headers=HEADERS).status_code == 409

    assert client.post("/api/train/run_1/cancel", headers=HEADERS).json()["phase"] == "cancelled"
    assert trainer._processes["run_1"].poll() is not None
    assert client.get("/api/train/run_1", headers=HEADERS).json()["phase"] == "cancelled"
    assert client.get("/api/status", headers=HEADERS).json() == {"running": None}

    monkeypatch.setenv("FAKE_YOLO_MODE", "ok")
    assert start(run_id="run_2").status_code == 200
    assert wait_for("run_2")["phase"] == "completed"


def test_rejects_out_of_range_parameters(dirs):
    assert start(epochs=0).status_code == 400
    assert start(imgsz=10_000).status_code == 400


def test_summarize_handles_missing_metrics():
    class Empty:
        results_dict = {"metrics/accuracy_top1": 0.9}

    summary = train_runner.summarize(Empty(), {0: "a"})
    assert summary == {"overall": {"metrics/accuracy_top1": 0.9}, "perClass": [], "speed": {}}


def test_non_finite_values_never_reach_json(tmp_path):
    class Metric:
        maps = [float("nan"), 0.5]
        ap_class_index = [0, 1]
        ap50 = [float("nan"), 0.7]

    class Result:
        results_dict = {"metrics/mAP50(B)": float("nan"), "fitness": 0.1}
        box = Metric()
        speed = {"inference": float("inf")}

    summary = train_runner.summarize(Result(), {0: "a", 1: "b"})
    json.dumps(summary, allow_nan=False)
    assert summary["overall"]["metrics/mAP50(B)"] is None
    assert summary["perClass"][0] == {"class": "a", "mAP50": None, "mAP50-95": None}

    (tmp_path / "train").mkdir()
    (tmp_path / "train" / "results.csv").write_text("epoch,train/box_loss\n1,nan\n2,0.5\n")
    assert trainer.read_history(tmp_path) == [{"epoch": 1.0}, {"epoch": 2.0, "train/box_loss": 0.5}]
