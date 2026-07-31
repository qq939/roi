from __future__ import annotations

import argparse
import csv
import math
import random
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image, ImageDraw

from clip_common import IMAGE_EXTENSIONS, list_images, resolve_device


MODEL_NAMES = {
    "vits14": "dinov2_vits14",
    "vitb14": "dinov2_vitb14",
    "vitl14": "dinov2_vitl14",
    "vitg14": "dinov2_vitg14",
}

IMAGENET_MEAN = np.asarray([0.485, 0.456, 0.406], dtype=np.float32)
IMAGENET_STD = np.asarray([0.229, 0.224, 0.225], dtype=np.float32)


@dataclass(frozen=True)
class DinoFeatures:
    image_path: Path
    global_feature: torch.Tensor
    patch_features: torch.Tensor
    grid_hw: tuple[int, int]
    original_size: tuple[int, int]


@dataclass(frozen=True)
class EncodedImage:
    image_path: Path
    tensor: torch.Tensor
    original_size: tuple[int, int]


def load_dino_model(model_key: str, device: torch.device):
    model_name = MODEL_NAMES.get(model_key, model_key)
    model = torch.hub.load("facebookresearch/dinov2", model_name, trust_repo=True)
    model.eval().to(device)
    return model


def preprocess_image(image_path: Path, image_size: int) -> EncodedImage:
    with Image.open(image_path) as image:
        image = image.convert("RGB")
        original_size = image.size
        scale = image_size / min(image.size)
        resized = (
            max(image_size, int(round(image.width * scale))),
            max(image_size, int(round(image.height * scale))),
        )
        image = image.resize(resized, Image.Resampling.BICUBIC)
        left = (image.width - image_size) // 2
        top = (image.height - image_size) // 2
        image = image.crop((left, top, left + image_size, top + image_size))
        array = np.asarray(image).astype(np.float32) / 255.0

    array = (array - IMAGENET_MEAN) / IMAGENET_STD
    tensor = torch.from_numpy(array).permute(2, 0, 1).contiguous()
    return EncodedImage(image_path=image_path, tensor=tensor, original_size=original_size)


def l2_normalize(tensor: torch.Tensor) -> torch.Tensor:
    return F.normalize(tensor.float(), dim=-1)


@torch.inference_mode()
def encode_one(model, image_path: Path, image_size: int, patch_size: int, device: torch.device) -> DinoFeatures:
    encoded = preprocess_image(image_path, image_size)
    batch = encoded.tensor.unsqueeze(0).to(device)
    output = model.forward_features(batch)
    global_feature = l2_normalize(output["x_norm_clstoken"][0])
    patch_features = l2_normalize(output["x_norm_patchtokens"][0])

    patch_count = patch_features.shape[0]
    grid = image_size // patch_size
    if grid * grid != patch_count:
        grid = int(math.sqrt(patch_count))
    grid_hw = (grid, max(1, patch_count // grid))

    return DinoFeatures(
        image_path=image_path,
        global_feature=global_feature.detach(),
        patch_features=patch_features.detach(),
        grid_hw=grid_hw,
        original_size=encoded.original_size,
    )


def build_patch_memory(
    ok_features: list[DinoFeatures],
    max_memory_patches: int | None,
    seed: int,
) -> torch.Tensor:
    memory = torch.cat([item.patch_features for item in ok_features], dim=0)
    if max_memory_patches is not None and memory.shape[0] > max_memory_patches:
        generator = random.Random(seed)
        indices = list(range(memory.shape[0]))
        generator.shuffle(indices)
        selected = torch.tensor(indices[:max_memory_patches], device=memory.device)
        memory = memory.index_select(0, selected)
    return memory.contiguous()


def topk_global_score(query: torch.Tensor, ok_globals: torch.Tensor, top_k: int) -> float:
    sims = ok_globals @ query
    k = min(top_k, sims.numel())
    return float(torch.topk(sims, k=k).values.mean().item())


def nearest_patch_distances(
    query_patches: torch.Tensor,
    memory_patches: torch.Tensor,
    chunk_size: int,
) -> torch.Tensor:
    distances: list[torch.Tensor] = []
    for start in range(0, query_patches.shape[0], chunk_size):
        chunk = query_patches[start : start + chunk_size]
        similarities = chunk @ memory_patches.T
        nearest = similarities.max(dim=1).values
        distances.append(1.0 - nearest)
    return torch.cat(distances, dim=0)


def nearest_patch_distances_by_position(
    query_patches: torch.Tensor,
    ok_patch_grids: torch.Tensor,
    grid_hw: tuple[int, int],
    window_radius: int,
) -> torch.Tensor:
    grid_h, grid_w = grid_hw
    query_grid = query_patches.reshape(grid_h, grid_w, -1)
    distances: list[torch.Tensor] = []
    for y in range(grid_h):
        for x in range(grid_w):
            y0 = max(0, y - window_radius)
            y1 = min(grid_h, y + window_radius + 1)
            x0 = max(0, x - window_radius)
            x1 = min(grid_w, x + window_radius + 1)
            candidates = ok_patch_grids[:, y0:y1, x0:x1, :].reshape(-1, ok_patch_grids.shape[-1])
            similarities = candidates @ query_grid[y, x]
            distances.append(1.0 - similarities.max().unsqueeze(0))
    return torch.cat(distances, dim=0)


def compute_patch_distances(
    query_patches: torch.Tensor,
    memory_patches: torch.Tensor,
    ok_patch_grids: torch.Tensor,
    grid_hw: tuple[int, int],
    match_mode: str,
    window_radius: int,
    chunk_size: int,
) -> torch.Tensor:
    if match_mode == "unrestricted":
        return nearest_patch_distances(query_patches, memory_patches, chunk_size)
    if match_mode == "same-position":
        return nearest_patch_distances_by_position(query_patches, ok_patch_grids, grid_hw, 0)
    if match_mode == "local-window":
        return nearest_patch_distances_by_position(query_patches, ok_patch_grids, grid_hw, window_radius)
    raise ValueError(f"Unsupported match mode: {match_mode}")


def patch_metrics(distances: torch.Tensor, anomaly_threshold: float) -> dict[str, float]:
    count = distances.numel()
    top_count = max(1, int(math.ceil(count * 0.05)))
    top_values = torch.topk(distances, k=top_count).values
    return {
        "patch_mean_distance": float(distances.mean().item()),
        "patch_top5_distance": float(top_values.mean().item()),
        "patch_max_distance": float(distances.max().item()),
        "patch_anomaly_area_ratio": float((distances >= anomaly_threshold).float().mean().item()),
    }


def filter_spatially_supported_distances(
    distances: torch.Tensor,
    grid_hw: tuple[int, int],
    top_percent: float,
    min_neighbors: int,
) -> tuple[torch.Tensor, torch.Tensor]:
    grid_h, grid_w = grid_hw
    count = distances.numel()
    candidate_count = max(1, int(math.ceil(count * top_percent)))
    candidate_indices = torch.topk(distances, k=candidate_count).indices
    flat_mask = torch.zeros(count, dtype=torch.bool, device=distances.device)
    flat_mask[candidate_indices] = True
    mask = flat_mask.reshape(1, 1, grid_h, grid_w)
    kernel = torch.ones((1, 1, 3, 3), dtype=torch.float32, device=distances.device)
    neighbor_counts = F.conv2d(mask.float(), kernel, padding=1) - mask.float()
    supported_mask = mask & (neighbor_counts >= float(min_neighbors))
    return distances.reshape(grid_h, grid_w)[supported_mask.reshape(grid_h, grid_w)], supported_mask.reshape(-1)


def spatial_filter_metrics(
    distances: torch.Tensor,
    grid_hw: tuple[int, int],
    top_percent: float,
    min_neighbors: int,
) -> dict[str, float]:
    filtered_distances, supported_mask = filter_spatially_supported_distances(
        distances,
        grid_hw,
        top_percent,
        min_neighbors,
    )
    if filtered_distances.numel() == 0:
        return {
            "filtered_patch_top5_distance": 0.0,
            "filtered_anomaly_area_ratio": 0.0,
            "filtered_patch_count": 0.0,
        }

    top_count = max(1, int(math.ceil(filtered_distances.numel() * 0.05)))
    top_values = torch.topk(filtered_distances, k=top_count).values
    return {
        "filtered_patch_top5_distance": float(top_values.mean().item()),
        "filtered_anomaly_area_ratio": float(supported_mask.float().mean().item()),
        "filtered_patch_count": float(filtered_distances.numel()),
    }


def label_from_global_score(global_score: float, threshold: float) -> str:
    return "OK" if global_score >= threshold else "NG"


def label_from_patch_distance(patch_top5_distance: float, threshold: float) -> str:
    return "NG" if patch_top5_distance >= threshold else "OK"


def hierarchical_decision(
    global_score: float,
    global_accept_threshold: float,
    global_reject_threshold: float,
) -> tuple[str | None, str]:
    if global_score >= global_accept_threshold:
        return "OK", "GlobalAccept"
    if global_score <= global_reject_threshold:
        return "NG", "GlobalReject"
    return None, "LocalReview"


def make_heatmap_overlay(
    image_path: Path,
    distances: torch.Tensor,
    grid_hw: tuple[int, int],
    output_path: Path,
    resample: Image.Resampling,
) -> None:
    values = distances.detach().cpu().numpy().reshape(grid_hw)
    min_value = float(values.min())
    max_value = float(values.max())
    normalized = (values - min_value) / max(max_value - min_value, 1e-6)
    heat = np.zeros((*normalized.shape, 3), dtype=np.uint8)
    heat[..., 0] = np.clip(normalized * 255, 0, 255).astype(np.uint8)
    heat[..., 1] = np.clip((1.0 - np.abs(normalized - 0.5) * 2.0) * 180, 0, 180).astype(np.uint8)
    heat[..., 2] = np.clip((1.0 - normalized) * 120, 0, 120).astype(np.uint8)

    with Image.open(image_path) as image:
        base = image.convert("RGB")
        heat_image = Image.fromarray(heat, mode="RGB").resize(base.size, resample)
        overlay = Image.blend(base, heat_image, alpha=0.45)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    overlay.save(output_path)


def make_heatmap_overlays(
    image_path: Path,
    distances: torch.Tensor,
    grid_hw: tuple[int, int],
    smooth_output_path: Path,
    block_output_path: Path,
) -> None:
    make_heatmap_overlay(
        image_path,
        distances,
        grid_hw,
        smooth_output_path,
        Image.Resampling.BICUBIC,
    )
    make_heatmap_overlay(
        image_path,
        distances,
        grid_hw,
        block_output_path,
        Image.Resampling.NEAREST,
    )


def make_contact_sheet(rows: list[dict[str, str | float]], output_dir: Path) -> Path:
    thumb_w, thumb_h = 180, 140
    label_h = 38
    root = Path.cwd()
    sheet = Image.new("RGB", (thumb_w * 2, (thumb_h + label_h) * len(rows)), "white")
    draw = ImageDraw.Draw(sheet)
    for index, row in enumerate(rows):
        y = index * (thumb_h + label_h)
        image_path = Path(str(row["image"]))
        heatmap_value = str(row.get("heatmap", ""))
        heatmap_path = Path(heatmap_value) if heatmap_value else None
        if not image_path.is_absolute():
            image_path = root / image_path
        if heatmap_path is not None and not heatmap_path.is_absolute():
            heatmap_path = root / heatmap_path

        with Image.open(image_path) as image:
            image = image.convert("RGB")
            image.thumbnail((thumb_w, thumb_h))
            sheet.paste(image, ((thumb_w - image.width) // 2, y))

        if heatmap_path is not None and heatmap_path.exists():
            with Image.open(heatmap_path) as heatmap:
                heatmap = heatmap.convert("RGB")
                heatmap.thumbnail((thumb_w, thumb_h))
                sheet.paste(heatmap, (thumb_w + (thumb_w - heatmap.width) // 2, y))
        else:
            draw.rectangle((thumb_w, y, thumb_w * 2 - 1, y + thumb_h - 1), fill=(242, 242, 242))
            draw.text((thumb_w + 36, y + 58), "local skipped", fill=(80, 80, 80))

        patch_text = "--"
        if row.get("patch_top5_distance") != "":
            patch_text = f"{float(row['patch_top5_distance']):.3f}"
        text = (
            f"{Path(str(row['image'])).name}  "
            f"g={float(row['dino_global_score']):.3f} "
            f"top5={patch_text} "
            f"{row.get('hierarchical_label', '')}/{row.get('decision_stage', '')}"
        )
        draw.text((4, y + thumb_h + 4), text, fill=(0, 0, 0))

    sheet_path = output_dir / "dino_patch_contact_sheet.jpg"
    sheet.save(sheet_path, quality=92)
    return sheet_path


def collect_images(path: Path) -> list[Path]:
    if path.is_file():
        if path.suffix.lower() not in IMAGE_EXTENSIONS:
            raise ValueError(f"Unsupported image extension: {path}")
        return [path]
    return list_images(path)


def run_experiment(args: argparse.Namespace) -> None:
    ok_paths = collect_images(args.ok_dir)
    query_paths = collect_images(args.query_dir)
    if not ok_paths:
        raise ValueError(f"No OK images found under {args.ok_dir}")
    if not query_paths:
        raise ValueError(f"No query images found under {args.query_dir}")

    device = resolve_device(args.device)
    output_dir = args.out_dir
    smooth_heatmap_dir = output_dir / "heatmaps_smooth"
    block_heatmap_dir = output_dir / "heatmaps_block"
    output_dir.mkdir(parents=True, exist_ok=True)

    print(f"Loading DINOv2 model: {args.model} on {device}")
    model = load_dino_model(args.model, device)

    print(f"Encoding OK images: {len(ok_paths)}")
    ok_features = [encode_one(model, path, args.image_size, args.patch_size, device) for path in ok_paths]
    ok_globals = torch.stack([item.global_feature for item in ok_features], dim=0)
    memory = build_patch_memory(ok_features, args.max_memory_patches, args.seed)
    grid_hw = ok_features[0].grid_hw
    if any(item.grid_hw != grid_hw for item in ok_features):
        raise ValueError("All OK images must produce the same patch grid.")
    ok_patch_grids = torch.stack(
        [item.patch_features.reshape(*grid_hw, -1) for item in ok_features],
        dim=0,
    ).contiguous()
    print(
        f"OK global: {tuple(ok_globals.shape)}, "
        f"patch memory: {tuple(memory.shape)}, "
        f"match_mode={args.match_mode}, radius={args.window_radius}"
    )

    rows: list[dict[str, str | float]] = []
    started = time.perf_counter()
    local_review_count = 0
    local_computed_count = 0
    global_only_threshold = args.global_only_threshold
    if global_only_threshold is None:
        global_only_threshold = (args.global_accept_threshold + args.global_reject_threshold) / 2.0

    for index, image_path in enumerate(query_paths, start=1):
        encode_started = time.perf_counter()
        features = encode_one(model, image_path, args.image_size, args.patch_size, device)
        elapsed_encode_ms = (time.perf_counter() - encode_started) * 1000.0
        if features.grid_hw != grid_hw:
            raise ValueError(f"Query image grid {features.grid_hw} does not match OK grid {grid_hw}: {image_path}")

        global_started = time.perf_counter()
        global_score = topk_global_score(features.global_feature, ok_globals, args.top_k)
        elapsed_global_match_ms = (time.perf_counter() - global_started) * 1000.0

        direct_label, decision_stage = hierarchical_decision(
            global_score,
            args.global_accept_threshold,
            args.global_reject_threshold,
        )
        needs_local_review = decision_stage == "LocalReview"
        should_compute_local = needs_local_review or args.evaluate_local_for_all
        if needs_local_review:
            local_review_count += 1

        metrics: dict[str, float | str] = {
            "patch_mean_distance": "",
            "patch_top5_distance": "",
            "patch_max_distance": "",
            "patch_anomaly_area_ratio": "",
            "filtered_patch_top5_distance": "",
            "filtered_anomaly_area_ratio": "",
            "filtered_patch_count": "",
            "local_decision_score": "",
        }
        smooth_heatmap_path: Path | None = None
        block_heatmap_path: Path | None = None
        local_only_label = ""
        elapsed_local_match_ms = 0.0

        if should_compute_local:
            local_started = time.perf_counter()
            distances = compute_patch_distances(
                features.patch_features,
                memory,
                ok_patch_grids,
                features.grid_hw,
                args.match_mode,
                args.window_radius,
                args.chunk_size,
            )
            metrics = patch_metrics(distances, args.anomaly_threshold)
            filter_metrics = spatial_filter_metrics(
                distances,
                features.grid_hw,
                args.spatial_filter_top_percent,
                args.min_neighbors,
            )
            metrics.update(filter_metrics)
            local_decision_score = (
                float(metrics["filtered_patch_top5_distance"])
                if args.spatial_filter
                else float(metrics["patch_top5_distance"])
            )
            metrics["local_decision_score"] = local_decision_score
            elapsed_local_match_ms = (time.perf_counter() - local_started) * 1000.0
            local_computed_count += 1
            local_only_label = label_from_patch_distance(
                local_decision_score,
                args.patch_top5_threshold,
            )

            heatmap_name = f"{image_path.stem}_{args.match_mode}_dino_heatmap.png"
            smooth_heatmap_path = smooth_heatmap_dir / heatmap_name
            block_heatmap_path = block_heatmap_dir / heatmap_name
            make_heatmap_overlays(
                image_path,
                distances,
                features.grid_hw,
                smooth_heatmap_path,
                block_heatmap_path,
            )

        hierarchical_label = direct_label if direct_label is not None else local_only_label
        global_only_label = label_from_global_score(global_score, global_only_threshold)

        row = {
            "image": str(image_path),
            "match_mode": args.match_mode,
            "window_radius": args.window_radius,
            "DecisionStage": decision_stage,
            "GlobalScore": global_score,
            "PatchTop5Distance": metrics["patch_top5_distance"],
            "PatchAnomalyAreaRatio": metrics["patch_anomaly_area_ratio"],
            "FilteredPatchTop5Distance": metrics["filtered_patch_top5_distance"],
            "FilteredAnomalyAreaRatio": metrics["filtered_anomaly_area_ratio"],
            "FilteredPatchCount": metrics["filtered_patch_count"],
            "LocalDecisionScore": metrics["local_decision_score"],
            "ElapsedEncodeMs": elapsed_encode_ms,
            "ElapsedGlobalMatchMs": elapsed_global_match_ms,
            "ElapsedLocalMatchMs": elapsed_local_match_ms,
            "decision_stage": decision_stage,
            "hierarchical_label": hierarchical_label,
            "global_only_label": global_only_label,
            "local_only_label": local_only_label,
            "dino_global_score": global_score,
            "global_accept_threshold": args.global_accept_threshold,
            "global_reject_threshold": args.global_reject_threshold,
            "global_only_threshold": global_only_threshold,
            "patch_top5_threshold": args.patch_top5_threshold,
            "spatial_filter": args.spatial_filter,
            "spatial_filter_top_percent": args.spatial_filter_top_percent,
            "min_neighbors": args.min_neighbors,
            **metrics,
            "elapsed_encode_ms": elapsed_encode_ms,
            "elapsed_global_match_ms": elapsed_global_match_ms,
            "elapsed_local_match_ms": elapsed_local_match_ms,
            "heatmap": "" if smooth_heatmap_path is None else str(smooth_heatmap_path),
            "smooth_heatmap": "" if smooth_heatmap_path is None else str(smooth_heatmap_path),
            "block_heatmap": "" if block_heatmap_path is None else str(block_heatmap_path),
        }
        rows.append(row)
        patch_text = "--"
        area_text = "--"
        if row["patch_top5_distance"] != "":
            patch_text = f"{float(row['local_decision_score']):.4f}"
            area_text = f"{float(row['patch_anomaly_area_ratio']):.3f}"
        print(
            f"[{index}/{len(query_paths)}] {image_path.name} "
            f"global={global_score:.4f} "
            f"top5={patch_text} "
            f"area={area_text} "
            f"stage={decision_stage} "
            f"label={hierarchical_label} "
            f"time={elapsed_encode_ms:.1f}/{elapsed_global_match_ms:.1f}/{elapsed_local_match_ms:.1f}ms"
        )

    csv_path = output_dir / "dino_patch_results.csv"
    with csv_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)
    contact_sheet_path = make_contact_sheet(rows, output_dir)

    elapsed_ms = (time.perf_counter() - started) * 1000.0
    print(f"Saved CSV: {csv_path}")
    print(f"Saved smooth heatmaps: {smooth_heatmap_dir}")
    print(f"Saved block heatmaps: {block_heatmap_dir}")
    print(f"Saved contact sheet: {contact_sheet_path}")
    print(
        "Reviewed by local: "
        f"{local_review_count}/{len(query_paths)} "
        f"(local computed: {local_computed_count}/{len(query_paths)})"
    )
    print(f"Elapsed: {elapsed_ms:.1f} ms")


def main() -> None:
    parser = argparse.ArgumentParser(description="DINOv2 global + patch anomaly experiment.")
    parser.add_argument("--ok-dir", type=Path, default=Path("demoImage/OK"))
    parser.add_argument("--query-dir", type=Path, default=Path("demoImage/predict"))
    parser.add_argument("--out-dir", type=Path, default=Path("python_demo/outputs/dino_patch"))
    parser.add_argument("--model", default="vits14", choices=sorted(MODEL_NAMES))
    parser.add_argument("--device", default="auto")
    parser.add_argument("--image-size", type=int, default=224)
    parser.add_argument("--patch-size", type=int, default=14)
    parser.add_argument("--top-k", type=int, default=2)
    parser.add_argument("--chunk-size", type=int, default=256)
    parser.add_argument("--max-memory-patches", type=int)
    parser.add_argument("--global-accept-threshold", type=float, default=0.96)
    parser.add_argument("--global-reject-threshold", type=float, default=0.84)
    parser.add_argument("--global-only-threshold", type=float)
    parser.add_argument("--patch-top5-threshold", type=float, default=0.26)
    parser.add_argument(
        "--match-mode",
        default="local-window",
        choices=["unrestricted", "same-position", "local-window"],
    )
    parser.add_argument("--window-radius", type=int, default=1)
    parser.add_argument("--anomaly-threshold", type=float, default=0.35)
    parser.add_argument(
        "--spatial-filter",
        action="store_true",
        help="Use neighborhood-supported patch distances for the local OK/NG decision.",
    )
    parser.add_argument(
        "--spatial-filter-top-percent",
        type=float,
        default=0.05,
        help="Fraction of strongest patch responses used as spatial-filter candidates.",
    )
    parser.add_argument(
        "--min-neighbors",
        type=int,
        default=2,
        help="Minimum anomalous 8-neighbors required to keep an anomalous patch in spatial filtering.",
    )
    parser.add_argument(
        "--evaluate-local-for-all",
        action="store_true",
        help="Compute local patch metrics for every query image to compare pure local and hierarchical decisions.",
    )
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()
    run_experiment(args)


if __name__ == "__main__":
    main()
