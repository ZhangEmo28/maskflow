"""Endpoint tests with stubbed models (no GPU, torch or ultralytics needed).

Run: python -m pytest src/ai/sam-inference/tests
"""
import hashlib
import io
import json
import os
import sys
import tempfile
from pathlib import Path

import numpy as np
import pytest
from PIL import Image

os.environ.setdefault("SAM3_INTERNAL_KEY", "test-internal-key")
os.environ.setdefault("SAM3_REQUIRE_INTERNAL_KEY", "true")
os.environ.setdefault("YOLO_MODEL_CACHE", tempfile.mkdtemp(prefix="maskflow-yolo-test-"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fastapi.testclient import TestClient  # noqa: E402

from app import main  # noqa: E402

HEADERS = {"X-Internal-Key": "test-internal-key"}
client = TestClient(main.app)


class FakeTensor:
    def __init__(self, value):
        self.value = np.asarray(value)

    def detach(self):
        return self

    def cpu(self):
        return self

    def numpy(self):
        return self.value


class FakeBoxes:
    def __init__(self, cls, conf, xyxyn=None):
        self.cls = FakeTensor(cls)
        self.conf = FakeTensor(conf)
        self.xyxyn = FakeTensor(xyxyn) if xyxyn is not None else None


class FakeMasks:
    def __init__(self, data, xyn=None):
        self.data = FakeTensor(data)
        self.xyn = xyn


class FakeResult:
    def __init__(self, masks=None, boxes=None, names=None):
        self.masks = masks
        self.boxes = boxes
        self.names = names or {}


def rect_mask(height, width, x0, y0, x1, y1):
    mask = np.zeros((height, width), dtype=np.float32)
    mask[y0:y1, x0:x1] = 1.0
    return mask


def png_bytes(width=100, height=100):
    buffer = io.BytesIO()
    Image.new("RGB", (width, height), (120, 130, 140)).save(buffer, format="PNG")
    return buffer.getvalue()


def test_internal_key_is_required():
    response = client.post("/api/annotation/concepts", files={"image": ("a.png", png_bytes(), "image/png")}, data={"prompts": '["car"]'})
    assert response.status_code == 401


def test_concepts_map_masks_to_prompts_with_tight_boxes(monkeypatch):
    class FakePredictor:
        args = type("Args", (), {"conf": 0.25})()

        def set_image(self, path):
            assert Path(path).exists()

        def __call__(self, text):
            assert text == ["car", "hard hat"]
            masks = np.stack([rect_mask(100, 100, 10, 20, 30, 40), rect_mask(100, 100, 50, 50, 90, 70)])
            return [FakeResult(FakeMasks(masks), FakeBoxes([1, 0], [0.9, 0.6]), names={0: "hard hat", 1: "car"})]

    monkeypatch.setattr(main, "_get_predictor", lambda conf, half: FakePredictor())
    response = client.post(
        "/api/annotation/concepts",
        headers=HEADERS,
        files={"image": ("a.png", png_bytes(), "image/png")},
        data={"prompts": json.dumps(["car", "hard hat"]), "conf": "0.3"},
    )
    assert response.status_code == 200, response.text
    body = response.json()
    assert body["count"] == 2
    car, hat = body["masks"]
    assert (car["promptIndex"], car["prompt"]) == (0, "car")
    assert hat["promptIndex"] == 1
    assert car["score"] == pytest.approx(0.9)
    # 20x20 px object in a 100 px image: the box must hug it (no display padding).
    assert car["yoloBox"]["width"] == pytest.approx(0.2, abs=0.03)
    assert car["yoloBox"]["cx"] == pytest.approx(0.2, abs=0.02)


@pytest.mark.parametrize("prompts", ["not json", "[]", json.dumps(["x"] * 51), json.dumps(["ok", ""])])
def test_concepts_reject_bad_prompts(prompts):
    response = client.post("/api/annotation/concepts", headers=HEADERS, files={"image": ("a.png", png_bytes(), "image/png")}, data={"prompts": prompts})
    assert response.status_code == 400


def test_annotation_masks_report_scores(monkeypatch):
    class FakeAuto:
        def predict(self, **kwargs):
            masks = np.stack([rect_mask(100, 100, 10, 10, 40, 40)])
            return [FakeResult(FakeMasks(masks), FakeBoxes([0], [0.77]))]

    monkeypatch.setattr(main, "_get_auto_model", lambda: FakeAuto())
    response = client.post("/api/annotation/masks", headers=HEADERS, files={"image": ("a.png", png_bytes(), "image/png")})
    assert response.status_code == 200, response.text
    mask = response.json()["masks"][0]
    assert mask["score"] == pytest.approx(0.77)
    assert mask["yoloBox"]["width"] == pytest.approx(0.3, abs=0.03)


def upload_model(model_id, content, name="best.onnx", sha=None):
    return client.put(
        f"/api/yolo/models/{model_id}",
        headers=HEADERS,
        files={"weights": (name, content, "application/octet-stream")},
        data={"sha256": sha or hashlib.sha256(content).hexdigest()},
    )


def test_model_upload_accepts_only_verified_onnx():
    assert upload_model("m1", b"pickle", name="best.pt").status_code == 400
    assert upload_model("m1", b"onnx-bytes", sha="0" * 64).status_code == 400
    assert upload_model("../evil", b"onnx-bytes").status_code in (400, 404)
    assert client.get("/api/yolo/models/m1", headers=HEADERS).json() == {"cached": False}

    response = upload_model("m1", b"onnx-bytes")
    assert response.status_code == 200
    status = client.get("/api/yolo/models/m1", headers=HEADERS).json()
    assert status["cached"] is True
    assert status["sha256"] == hashlib.sha256(b"onnx-bytes").hexdigest()


def test_yolo_predict_returns_normalized_detections(monkeypatch):
    class FakeYolo:
        def predict(self, **kwargs):
            boxes = FakeBoxes([1, 0], [0.8, 0.4], xyxyn=[[0.1, 0.2, 0.3, 0.6], [0.5, 0.5, 1.2, 0.9]])
            polygon = np.array([[0.1, 0.2], [0.3, 0.2], [0.3, 0.6]])
            return [FakeResult(FakeMasks(np.zeros((2, 4, 4)), xyn=[polygon, np.zeros((0, 2))]), boxes, names={0: "person", 1: "car"})]

    monkeypatch.setattr(main, "_load_yolo_model", lambda path: FakeYolo())
    assert client.post("/api/yolo/predict", headers=HEADERS, files={"image": ("a.png", png_bytes(), "image/png")}, data={"model_id": "missing"}).status_code == 404

    upload_model("m2", b"model-two")
    response = client.post("/api/yolo/predict", headers=HEADERS, files={"image": ("a.png", png_bytes(200, 100), "image/png")}, data={"model_id": "m2"})
    assert response.status_code == 200, response.text
    body = response.json()
    assert body["classNames"] == ["person", "car"]
    first, second = body["detections"]
    assert first["className"] == "car" and first["score"] == pytest.approx(0.8)
    assert first["yoloBox"] == pytest.approx({"cx": 0.2, "cy": 0.4, "width": 0.2, "height": 0.4})
    assert len(first["yoloSegments"][0]) == 6
    assert second["yoloBox"]["width"] == pytest.approx(0.5)  # clamped to the image
    assert second["yoloSegments"] == []
