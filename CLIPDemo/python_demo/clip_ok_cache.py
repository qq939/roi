from __future__ import annotations

import argparse
import json
import time
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np

from clip_common import ClipConfig, encode_images, encode_texts, list_images, load_clip


@dataclass
class CacheItem:
    imagePath: str
    feature: list[float]


@dataclass
class TextCacheItem:
    prompt: str
    feature: list[float]


@dataclass
class OkCache:
    productId: str
    modelName: str
    pretrained: str
    featureDim: int
    topK: int
    threshold: float
    items: list[CacheItem]
    ngItems: list[CacheItem] | None = None
    okTextItems: list[TextCacheItem] | None = None
    ngTextItems: list[TextCacheItem] | None = None
    textWeight: float = 0.2


def save_cache(cache: OkCache, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as f:
        json.dump(asdict(cache), f, indent=2)


def load_cache(path: Path) -> OkCache:
    with path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    ok_items = data.get("okItems", data.get("items", []))
    ng_items = data.get("ngItems", [])
    ok_text_items = data.get("okTextItems", [])
    ng_text_items = data.get("ngTextItems", [])
    return OkCache(
        productId=data["productId"],
        modelName=data.get("modelName", "ViT-B-32"),
        pretrained=data.get("pretrained", "laion2b_s34b_b79k"),
        featureDim=data["featureDim"],
        topK=data["topK"],
        threshold=data["threshold"],
        items=[CacheItem(**item) for item in ok_items],
        ngItems=[CacheItem(**item) for item in ng_items],
        okTextItems=[TextCacheItem(**item) for item in ok_text_items],
        ngTextItems=[TextCacheItem(**item) for item in ng_text_items],
        textWeight=float(data.get("textWeight", 0.2)),
    )


def cache_items_from_features(image_paths: list[Path], features: np.ndarray) -> list[CacheItem]:
    return [
        CacheItem(str(path.resolve()), feature.astype(float).tolist())
        for path, feature in zip(image_paths, features)
    ]


def text_items_from_features(prompts: list[str], features: np.ndarray) -> list[TextCacheItem]:
    return [
        TextCacheItem(prompt=prompt, feature=feature.astype(float).tolist())
        for prompt, feature in zip(prompts, features)
    ]


def clean_prompts(prompts: list[str] | None) -> list[str]:
    if not prompts:
        return []
    return [prompt.strip() for prompt in prompts if prompt and prompt.strip()]


def build_ok_cache(
    ok_dir: Path,
    product_id: str,
    out: Path,
    top_k: int,
    threshold: float,
    ng_dir: Path | None = None,
    ok_text_prompts: list[str] | None = None,
    ng_text_prompts: list[str] | None = None,
    text_weight: float = 0.2,
    model_name: str = "ViT-B-32",
    pretrained: str = "laion2b_s34b_b79k",
    device_name: str = "auto",
    batch_size: int = 16,
    clip_runtime=None,
) -> OkCache:
    image_paths = list_images(ok_dir)
    if not image_paths:
        raise ValueError(f"No images found under {ok_dir}")

    if clip_runtime is None:
        model, preprocess, tokenizer, device = load_clip(ClipConfig(model_name, pretrained, device_name))
    else:
        model, preprocess, tokenizer, device = clip_runtime

    features = encode_images(model, preprocess, device, image_paths, batch_size)
    items = cache_items_from_features(image_paths, features)
    ng_items: list[CacheItem] = []
    if ng_dir is not None:
        ng_paths = list_images(ng_dir)
        if ng_paths:
            ng_features = encode_images(model, preprocess, device, ng_paths, batch_size)
            ng_items = cache_items_from_features(ng_paths, ng_features)

    ok_prompts = clean_prompts(ok_text_prompts)
    ng_prompts = clean_prompts(ng_text_prompts)
    ok_text_items = text_items_from_features(ok_prompts, encode_texts(model, tokenizer, device, ok_prompts)) if ok_prompts else []
    ng_text_items = text_items_from_features(ng_prompts, encode_texts(model, tokenizer, device, ng_prompts)) if ng_prompts else []

    cache = OkCache(
        productId=product_id,
        modelName=model_name,
        pretrained=pretrained,
        featureDim=int(features.shape[1]),
        topK=top_k,
        threshold=threshold,
        items=items,
        ngItems=ng_items,
        okTextItems=ok_text_items,
        ngTextItems=ng_text_items,
        textWeight=max(0.0, min(1.0, text_weight)),
    )
    save_cache(cache, out)
    return cache


def topk_scores(query_feature: np.ndarray, items: list[CacheItem], top_k: int) -> tuple[float, list[dict]]:
    features = np.asarray([item.feature for item in items], dtype=np.float32)
    similarities = features @ query_feature
    order = np.argsort(-similarities)[: min(top_k, len(items))]
    score = float(np.mean(similarities[order]))
    top_items = [
        {
            "rank": rank,
            "similarity": float(similarities[index]),
            "imagePath": items[index].imagePath,
        }
        for rank, index in enumerate(order, start=1)
    ]
    return score, top_items


def topk_text_scores(query_feature: np.ndarray, items: list[TextCacheItem], top_k: int) -> tuple[float, list[dict]]:
    features = np.asarray([item.feature for item in items], dtype=np.float32)
    similarities = features @ query_feature
    order = np.argsort(-similarities)[: min(top_k, len(items))]
    score = float(np.mean(similarities[order]))
    top_items = [
        {
            "rank": rank,
            "similarity": float(similarities[index]),
            "prompt": items[index].prompt,
        }
        for rank, index in enumerate(order, start=1)
    ]
    return score, top_items


def blend_margins(image_margin: float | None, text_margin: float | None, text_weight: float) -> float | None:
    if image_margin is None and text_margin is None:
        return None
    if text_margin is None:
        return image_margin
    if image_margin is None:
        return text_margin
    return (1.0 - text_weight) * image_margin + text_weight * text_margin


def detect_image(
    cache_path: Path,
    image_path: Path,
    top_k: int | None = None,
    threshold: float | None = None,
    model_name: str | None = None,
    pretrained: str | None = None,
    device_name: str = "auto",
    batch_size: int = 16,
    clip_runtime=None,
) -> dict:
    cache = load_cache(cache_path)
    if not cache.items:
        raise ValueError("Cache has no items.")

    resolved_top_k = min(top_k or cache.topK, len(cache.items))
    resolved_threshold = threshold if threshold is not None else cache.threshold
    resolved_model = model_name or cache.modelName
    resolved_pretrained = pretrained or cache.pretrained

    if clip_runtime is None:
        model, preprocess, _, device = load_clip(ClipConfig(resolved_model, resolved_pretrained, device_name))
    else:
        model, preprocess, _, device = clip_runtime

    inference_start = time.perf_counter()
    query_feature = encode_images(model, preprocess, device, [image_path], batch_size)[0]
    inference_ms = (time.perf_counter() - inference_start) * 1000.0

    match_start = time.perf_counter()
    image_ok_score, top_ok = topk_scores(query_feature, cache.items, resolved_top_k)
    image_ng_score = None
    text_ok_score = None
    text_ng_score = None
    image_margin = None
    text_margin = None
    margin = None
    top_ng: list[dict] = []
    top_text_ok: list[dict] = []
    top_text_ng: list[dict] = []
    ng_items = cache.ngItems or []
    if ng_items:
        image_ng_score, top_ng = topk_scores(query_feature, ng_items, resolved_top_k)

    ok_text_items = cache.okTextItems or []
    ng_text_items = cache.ngTextItems or []
    if ok_text_items:
        text_ok_score, top_text_ok = topk_text_scores(query_feature, ok_text_items, resolved_top_k)
    if ng_text_items:
        text_ng_score, top_text_ng = topk_text_scores(query_feature, ng_text_items, resolved_top_k)

    text_weight = max(0.0, min(1.0, cache.textWeight))
    ok_score = image_ok_score
    ng_score = image_ng_score
    if image_ng_score is not None:
        image_margin = image_ok_score - image_ng_score
    if text_ok_score is not None and text_ng_score is not None:
        text_margin = text_ok_score - text_ng_score
    margin = blend_margins(image_margin, text_margin, text_weight)

    if margin is not None:
        result = "OK" if image_ok_score >= resolved_threshold and margin >= 0.0 else "NG"
    else:
        result = "OK" if image_ok_score >= resolved_threshold else "NG"
    match_ms = (time.perf_counter() - match_start) * 1000.0

    return {
        "productId": cache.productId,
        "imagePath": str(image_path.resolve()),
        "score": ok_score,
        "okScore": ok_score,
        "ngScore": ng_score,
        "imageOkScore": image_ok_score,
        "imageNgScore": image_ng_score,
        "textOkScore": text_ok_score,
        "textNgScore": text_ng_score,
        "imageMargin": image_margin,
        "textMargin": text_margin,
        "margin": margin,
        "threshold": float(resolved_threshold),
        "result": result,
        "topK": top_ok,
        "topNgK": top_ng,
        "topTextOkK": top_text_ok,
        "topTextNgK": top_text_ng,
        "featureDim": cache.featureDim,
        "cacheItems": len(cache.items),
        "ngCacheItems": len(ng_items),
        "okTextItems": len(ok_text_items),
        "ngTextItems": len(ng_text_items),
        "textWeight": text_weight,
        "timing": {
            "inferenceMs": inference_ms,
            "matchMs": match_ms,
            "totalMs": inference_ms + match_ms,
        },
    }


def build_cache(args: argparse.Namespace) -> None:
    cache = build_ok_cache(
        ok_dir=args.ok_dir,
        product_id=args.product_id,
        out=args.out,
        top_k=args.top_k,
        threshold=args.threshold,
        ng_dir=args.ng_dir,
        ok_text_prompts=args.ok_text,
        ng_text_prompts=args.ng_text,
        text_weight=args.text_weight,
        model_name=args.model,
        pretrained=args.pretrained,
        device_name=args.device,
        batch_size=args.batch_size,
    )
    print(f"Saved OK cache: {args.out.resolve()}")
    print(f"Images: {len(cache.items)}, feature_dim: {cache.featureDim}")


def detect(args: argparse.Namespace) -> None:
    result = detect_image(
        cache_path=args.cache,
        image_path=args.image,
        top_k=args.top_k,
        threshold=args.threshold,
        model_name=args.model,
        pretrained=args.pretrained,
        device_name=args.device,
        batch_size=args.batch_size,
    )

    print(f"product_id: {result['productId']}")
    print(f"image: {result['imagePath']}")
    print(f"score: {result['score']:.4f}")
    print(f"image_ok_score: {result['imageOkScore']:.4f}")
    if result["textOkScore"] is not None:
        print(f"text_ok_score: {result['textOkScore']:.4f}")
    if result["ngScore"] is not None:
        print(f"ng_score: {result['ngScore']:.4f}")
    print(f"threshold: {result['threshold']:.4f}")
    print(f"result: {result['result']}")
    print(f"inference_ms: {result['timing']['inferenceMs']:.2f}")
    print(f"match_ms: {result['timing']['matchMs']:.2f}")
    print("top_k:")
    for item in result["topK"]:
        print(f"  {item['rank']}. {item['similarity']:.4f}  {item['imagePath']}")


def compare(args: argparse.Namespace) -> None:
    config = ClipConfig(args.model, args.pretrained, args.device)
    model, preprocess, _, device = load_clip(config)
    features = encode_images(model, preprocess, device, [args.image_a, args.image_b], args.batch_size)
    similarity = float(features[0] @ features[1])
    print(f"{args.image_a.resolve()} vs {args.image_b.resolve()}")
    print(f"similarity: {similarity:.4f}")


def main() -> None:
    parser = argparse.ArgumentParser(description="CLIP + OK cache anomaly demo.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    def add_clip_options(p: argparse.ArgumentParser) -> None:
        p.add_argument("--model", default="ViT-B-32")
        p.add_argument("--pretrained", default="laion2b_s34b_b79k")
        p.add_argument("--device", default="auto")
        p.add_argument("--batch-size", type=int, default=16)

    build = subparsers.add_parser("build-cache", help="Build OK image feature cache.")
    build.add_argument("--ok-dir", type=Path, required=True)
    build.add_argument("--product-id", required=True)
    build.add_argument("--out", type=Path, required=True)
    build.add_argument("--ng-dir", type=Path)
    build.add_argument("--ok-text", action="append", default=[])
    build.add_argument("--ng-text", action="append", default=[])
    build.add_argument("--text-weight", type=float, default=0.2)
    build.add_argument("--top-k", type=int, default=3)
    build.add_argument("--threshold", type=float, default=0.82)
    add_clip_options(build)
    build.set_defaults(func=build_cache)

    detect_parser = subparsers.add_parser("detect", help="Detect one image against an OK cache.")
    detect_parser.add_argument("--cache", type=Path, required=True)
    detect_parser.add_argument("--image", type=Path, required=True)
    detect_parser.add_argument("--top-k", type=int)
    detect_parser.add_argument("--threshold", type=float)
    detect_parser.add_argument("--model")
    detect_parser.add_argument("--pretrained")
    detect_parser.add_argument("--device", default="auto")
    detect_parser.add_argument("--batch-size", type=int, default=16)
    detect_parser.set_defaults(func=detect)

    compare_parser = subparsers.add_parser("compare", help="Compute cosine similarity for two images.")
    compare_parser.add_argument("image_a", type=Path)
    compare_parser.add_argument("image_b", type=Path)
    add_clip_options(compare_parser)
    compare_parser.set_defaults(func=compare)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
