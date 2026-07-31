# Python CLIP Prototype

This folder contains a minimal Python pass for the plan in `CLIP_OK_Cache_Demo_Plan.md`.

## Setup

```powershell
conda run -n yolo pip install -r python_demo/requirements.txt
```

## Web UI

```powershell
conda run -n yolo python python_demo/web_app.py
```

Open:

```text
http://127.0.0.1:7860/
```

The UI supports:

- Picking an OK image folder from a system dialog and uploading it to the local Flask service.
- Picking one query image from a system dialog and detecting it against the selected OK cache.
- Manual path entry as a fallback.
- Feature curve inspection at `http://127.0.0.1:7860/features`.

## Create synthetic demo images

```powershell
conda run -n yolo python python_demo/make_demo_images.py --out Samples
```

## CLIP OK cache

Compare two images:

```powershell
conda run -n yolo python python_demo/clip_ok_cache.py compare Samples/part_A/ok/ok_01.png Samples/part_A/test/ok_like.png
```

Build an OK cache:

```powershell
conda run -n yolo python python_demo/clip_ok_cache.py build-cache --ok-dir Samples/part_A/ok --product-id part_A --out Cache/part_A.cache.json --top-k 3 --threshold 0.82
```

Build an OK + NG cache:

```powershell
conda run -n yolo python python_demo/clip_ok_cache.py build-cache --ok-dir Samples/part_A/ok --ng-dir Samples/part_A/test --product-id part_A_dual --out Cache/part_A_dual.cache.json --top-k 3 --threshold 0.99
```

Build an OK + NG + text prompt cache:

```powershell
conda run -n yolo python python_demo/clip_ok_cache.py build-cache --ok-dir Samples/part_A/ok --ng-dir Samples/part_A/test --product-id part_A_text --out Cache/part_A_text.cache.json --top-k 3 --threshold 0.99 --ok-text "a correctly assembled industrial part with all screws installed" --ng-text "a defective industrial part with a missing screw" --text-weight 0.2
```

Detect one image:

```powershell
conda run -n yolo python python_demo/clip_ok_cache.py detect --cache Cache/part_A.cache.json --image Samples/part_A/test/missing_connector.png
```

## Tip-Adapter style classifier

Build a few-shot cache from `train_root/class_name/images`:

```powershell
conda run -n yolo python python_demo/tip_adapter.py build-cache --train-root Samples/tip_adapter/train --out Cache/tip_adapter.cache.json
```

Predict one image:

```powershell
conda run -n yolo python python_demo/tip_adapter.py predict --cache Cache/tip_adapter.cache.json --image Samples/tip_adapter/test/blue_query.png
```
