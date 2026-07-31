from __future__ import annotations

import mimetypes
import shutil
from functools import lru_cache
from pathlib import Path
from urllib.parse import unquote

import torch
from flask import Flask, jsonify, render_template, request, send_file
from werkzeug.utils import secure_filename

from clip_common import ClipConfig, load_clip
from clip_ok_cache import build_ok_cache, detect_image, load_cache


ROOT = Path(__file__).resolve().parents[1]
CACHE_DIR = ROOT / "Cache"
UPLOAD_DIR = ROOT / "Uploads"
DEFAULT_MODEL = "ViT-B-32"
DEFAULT_PRETRAINED = "laion2b_s34b_b79k"

app = Flask(__name__, template_folder="templates", static_folder="static")


@lru_cache(maxsize=4)
def get_clip_runtime(model_name: str, pretrained: str, device_name: str):
    return load_clip(ClipConfig(model_name, pretrained, device_name))


def json_error(message: str, status_code: int = 400):
    response = jsonify({"ok": False, "error": message})
    response.status_code = status_code
    return response


def require_path(value: str | None, label: str) -> Path:
    if not value or not value.strip():
        raise ValueError(f"{label} is required.")
    return Path(value.strip()).expanduser()


def cache_summaries() -> list[dict]:
    CACHE_DIR.mkdir(exist_ok=True)
    summaries = []
    for path in sorted(CACHE_DIR.glob("*.cache.json")):
        try:
            cache = load_cache(path)
            summaries.append(
                {
                    "path": str(path),
                    "name": path.name,
                    "productId": cache.productId,
                    "items": len(cache.items),
                    "ngItems": len(cache.ngItems or []),
                    "okTextItems": len(cache.okTextItems or []),
                    "ngTextItems": len(cache.ngTextItems or []),
                    "textWeight": cache.textWeight,
                    "threshold": cache.threshold,
                    "topK": cache.topK,
                    "featureDim": cache.featureDim,
                }
            )
        except Exception:
            continue
    return summaries


def clean_name(name: str, fallback: str = "image") -> str:
    safe_name = secure_filename(name)
    return safe_name or fallback


def reset_dir(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)


@app.get("/")
def index():
    return render_template(
        "index.html",
        root=str(ROOT),
        default_ok_dir=str(ROOT / "Samples" / "part_A" / "ok"),
        default_test_image=str(ROOT / "Samples" / "part_A" / "test" / "ok_like.png"),
        default_cache=str(ROOT / "Cache" / "part_A.cache.json"),
    )


@app.get("/features")
def features():
    return render_template("features.html")


@app.get("/api/status")
def status():
    device = "cuda" if torch.cuda.is_available() else "cpu"
    device_name = torch.cuda.get_device_name(0) if torch.cuda.is_available() else "CPU"
    return jsonify(
        {
            "ok": True,
            "root": str(ROOT),
            "device": device,
            "deviceName": device_name,
            "torch": torch.__version__,
            "caches": cache_summaries(),
        }
    )


@app.get("/api/cache-features")
def api_cache_features():
    try:
        cache_path = require_path(request.args.get("cachePath"), "Cache path")
        if not cache_path.exists():
            return json_error(f"Cache does not exist: {cache_path}", 404)

        cache = load_cache(cache_path)
        samples = []
        for label, items in (("OK", cache.items), ("NG", cache.ngItems or [])):
            for index, item in enumerate(items, start=1):
                samples.append(
                    {
                        "id": f"{label.lower()}-{index}",
                        "label": label,
                        "index": index,
                        "imagePath": item.imagePath,
                        "fileName": Path(item.imagePath).name,
                        "feature": item.feature,
                    }
                )

        return jsonify(
            {
                "ok": True,
                "cachePath": str(cache_path.resolve()),
                "productId": cache.productId,
                "featureDim": cache.featureDim,
                "samples": samples,
            }
        )
    except Exception as exc:
        return json_error(str(exc), 500)


@app.post("/api/build-cache")
def api_build_cache():
    try:
        data = request.get_json(force=True)
        product_id = data.get("productId", "part_A").strip() or "part_A"
        ok_dir = require_path(data.get("okDir"), "OK folder")
        if not ok_dir.exists() or not ok_dir.is_dir():
            return json_error(f"OK folder does not exist: {ok_dir}")
        ng_dir = None
        if data.get("ngDir"):
            ng_dir = require_path(data.get("ngDir"), "NG folder")
            if not ng_dir.exists() or not ng_dir.is_dir():
                return json_error(f"NG folder does not exist: {ng_dir}")

        cache_path = Path(data.get("cachePath") or CACHE_DIR / f"{product_id}.cache.json")
        top_k = int(data.get("topK") or 3)
        threshold = float(data.get("threshold") or 0.82)
        ok_text_prompts = data.get("okTextPrompts") or []
        ng_text_prompts = data.get("ngTextPrompts") or []
        text_weight = float(data.get("textWeight") or 0.2)
        model_name = data.get("modelName") or DEFAULT_MODEL
        pretrained = data.get("pretrained") or DEFAULT_PRETRAINED
        device_name = data.get("device") or "auto"
        runtime = get_clip_runtime(model_name, pretrained, device_name)

        cache = build_ok_cache(
            ok_dir=ok_dir,
            product_id=product_id,
            out=cache_path,
            top_k=top_k,
            threshold=threshold,
            ng_dir=ng_dir,
            ok_text_prompts=ok_text_prompts,
            ng_text_prompts=ng_text_prompts,
            text_weight=text_weight,
            model_name=model_name,
            pretrained=pretrained,
            device_name=device_name,
            clip_runtime=runtime,
        )
        return jsonify(
            {
                "ok": True,
                "cachePath": str(cache_path.resolve()),
                "productId": cache.productId,
                "items": len(cache.items),
                "ngItems": len(cache.ngItems or []),
                "okTextItems": len(cache.okTextItems or []),
                "ngTextItems": len(cache.ngTextItems or []),
                "featureDim": cache.featureDim,
                "caches": cache_summaries(),
            }
        )
    except Exception as exc:
        return json_error(str(exc), 500)


@app.post("/api/detect")
def api_detect():
    try:
        data = request.get_json(force=True)
        cache_path = require_path(data.get("cachePath"), "Cache path")
        image_path = require_path(data.get("imagePath"), "Test image")
        if not cache_path.exists():
            return json_error(f"Cache does not exist: {cache_path}")
        if not image_path.exists() or not image_path.is_file():
            return json_error(f"Image does not exist: {image_path}")

        cache = load_cache(cache_path)
        model_name = data.get("modelName") or cache.modelName
        pretrained = data.get("pretrained") or cache.pretrained
        device_name = data.get("device") or "auto"
        threshold = data.get("threshold")
        runtime = get_clip_runtime(model_name, pretrained, device_name)

        result = detect_image(
            cache_path=cache_path,
            image_path=image_path,
            top_k=int(data["topK"]) if data.get("topK") else None,
            threshold=float(threshold) if threshold not in (None, "") else None,
            model_name=model_name,
            pretrained=pretrained,
            device_name=device_name,
            clip_runtime=runtime,
        )
        return jsonify({"ok": True, **result})
    except Exception as exc:
        return json_error(str(exc), 500)


@app.post("/api/upload-image")
def api_upload_image():
    try:
        file = request.files.get("image")
        if file is None or not file.filename:
            return json_error("No image was selected.")

        target_dir = UPLOAD_DIR / "query"
        target_dir.mkdir(parents=True, exist_ok=True)
        target = target_dir / clean_name(file.filename)
        file.save(target)

        return jsonify({"ok": True, "imagePath": str(target.resolve()), "fileName": file.filename})
    except Exception as exc:
        return json_error(str(exc), 500)


@app.post("/api/upload-ok-folder")
def api_upload_ok_folder():
    return upload_labeled_folder("ok")


@app.post("/api/upload-ng-folder")
def api_upload_ng_folder():
    return upload_labeled_folder("ng")


def upload_labeled_folder(label: str):
    try:
        files = request.files.getlist("images")
        if not files:
            return json_error(f"No {label.upper()} images were selected.")

        product_id = request.form.get("productId", "part_A").strip() or "part_A"
        target_dir = UPLOAD_DIR / label / clean_name(product_id, "part_A")
        reset_dir(target_dir)

        saved = []
        for file in files:
            if not file.filename:
                continue
            source_name = file.filename.replace("\\", "/")
            parts = [clean_name(part) for part in source_name.split("/") if part]
            filename = "__".join(parts) if parts else clean_name(file.filename)
            target = target_dir / filename
            file.save(target)
            saved.append(str(target.resolve()))

        if not saved:
            return json_error("No OK images were saved.")

        return jsonify(
            {
                "ok": True,
                f"{label}Dir": str(target_dir.resolve()),
                "count": len(saved),
                "sample": saved[:5],
            }
        )
    except Exception as exc:
        return json_error(str(exc), 500)


@app.get("/api/image")
def image():
    raw_path = request.args.get("path")
    if not raw_path:
        return json_error("path is required.")

    path = Path(unquote(raw_path))
    if not path.exists() or not path.is_file():
        return json_error(f"Image does not exist: {path}", 404)

    mimetype = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
    return send_file(path, mimetype=mimetype)


if __name__ == "__main__":
    app.run(host="127.0.0.1", port=7860, debug=False)
