from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np

from clip_common import ClipConfig, encode_images, encode_texts, list_images, load_clip


@dataclass
class TipAdapterCache:
    classes: list[str]
    modelName: str
    pretrained: str
    imagePaths: list[str]
    labels: list[int]
    keys: list[list[float]]
    values: list[list[float]]


def save_cache(cache: TipAdapterCache, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as f:
        json.dump(asdict(cache), f, indent=2)


def load_cache(path: Path) -> TipAdapterCache:
    with path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    return TipAdapterCache(**data)


def class_folders(root: Path) -> list[Path]:
    return sorted(p for p in root.iterdir() if p.is_dir())


def build_cache(args: argparse.Namespace) -> None:
    folders = class_folders(args.train_root)
    if not folders:
        raise SystemExit(f"No class folders found under {args.train_root}")

    classes = [folder.name for folder in folders]
    image_paths: list[Path] = []
    labels: list[int] = []
    for label, folder in enumerate(folders):
        paths = list_images(folder)
        image_paths.extend(paths)
        labels.extend([label] * len(paths))

    if not image_paths:
        raise SystemExit(f"No training images found under {args.train_root}")

    config = ClipConfig(args.model, args.pretrained, args.device)
    model, preprocess, _, device = load_clip(config)
    keys = encode_images(model, preprocess, device, image_paths, args.batch_size)
    values = np.eye(len(classes), dtype=np.float32)[labels]

    cache = TipAdapterCache(
        classes=classes,
        modelName=args.model,
        pretrained=args.pretrained,
        imagePaths=[str(path.resolve()) for path in image_paths],
        labels=labels,
        keys=keys.astype(float).tolist(),
        values=values.astype(float).tolist(),
    )
    save_cache(cache, args.out)
    print(f"Saved Tip-Adapter cache: {args.out.resolve()}")
    print(f"Classes: {', '.join(classes)}")
    print(f"Images: {len(image_paths)}, device: {device}")


def predict(args: argparse.Namespace) -> None:
    cache = load_cache(args.cache)
    config = ClipConfig(args.model or cache.modelName, args.pretrained or cache.pretrained, args.device)
    model, preprocess, tokenizer, device = load_clip(config)

    image_feature = encode_images(model, preprocess, device, [args.image], args.batch_size)
    prompts = [args.prompt.format(class_name=class_name.replace("_", " ")) for class_name in cache.classes]
    text_features = encode_texts(model, tokenizer, device, prompts)

    clip_logits = args.clip_scale * (image_feature @ text_features.T)
    keys = np.asarray(cache.keys, dtype=np.float32)
    values = np.asarray(cache.values, dtype=np.float32)
    affinity = image_feature @ keys.T
    cache_logits = np.exp(-args.beta * (1.0 - affinity)) @ values
    tip_logits = clip_logits + args.alpha * cache_logits

    probs = softmax(tip_logits[0])
    order = np.argsort(-probs)

    print(f"image: {args.image.resolve()}")
    print(f"prompt: {args.prompt}")
    print("predictions:")
    for index in order:
        print(f"  {cache.classes[index]}: {probs[index]:.4f}")
    print(f"predicted_class: {cache.classes[order[0]]}")


def softmax(logits: np.ndarray) -> np.ndarray:
    shifted = logits - np.max(logits)
    exp = np.exp(shifted)
    return exp / np.sum(exp)


def main() -> None:
    parser = argparse.ArgumentParser(description="Minimal Tip-Adapter style few-shot CLIP demo.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    build = subparsers.add_parser("build-cache", help="Build class cache from train_root/class_name/*.png.")
    build.add_argument("--train-root", type=Path, required=True)
    build.add_argument("--out", type=Path, required=True)
    build.add_argument("--model", default="ViT-B-32")
    build.add_argument("--pretrained", default="laion2b_s34b_b79k")
    build.add_argument("--device", default="auto")
    build.add_argument("--batch-size", type=int, default=16)
    build.set_defaults(func=build_cache)

    predict_parser = subparsers.add_parser("predict", help="Predict one image with CLIP + Tip-Adapter cache.")
    predict_parser.add_argument("--cache", type=Path, required=True)
    predict_parser.add_argument("--image", type=Path, required=True)
    predict_parser.add_argument("--model")
    predict_parser.add_argument("--pretrained")
    predict_parser.add_argument("--device", default="auto")
    predict_parser.add_argument("--batch-size", type=int, default=16)
    predict_parser.add_argument("--prompt", default="a photo of a {class_name}")
    predict_parser.add_argument("--clip-scale", type=float, default=100.0)
    predict_parser.add_argument("--alpha", type=float, default=2.0)
    predict_parser.add_argument("--beta", type=float, default=5.5)
    predict_parser.set_defaults(func=predict)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
