from __future__ import annotations

import argparse
import csv
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
from sklearn.covariance import LedoitWolf
from sklearn.decomposition import PCA

from clip_common import list_images
from clip_feature_clustering import encode_features


def l2_normalize(features: np.ndarray) -> np.ndarray:
    norms = np.linalg.norm(features, axis=1, keepdims=True)
    return features / np.clip(norms, 1e-12, None)


def collect_labeled_images(root: Path) -> tuple[list[Path], np.ndarray]:
    ok_dir = root / "ok"
    ng_dir = root / "ng"
    if not ok_dir.exists():
        raise ValueError(f"Missing OK folder: {ok_dir}")
    paths: list[Path] = []
    labels: list[str] = []
    for label, folder in [("ok", ok_dir), ("ng", ng_dir)]:
        if not folder.exists():
            continue
        for path in list_images(folder):
            paths.append(path)
            labels.append(label)
    if not paths:
        raise ValueError(f"No images found under {root}")
    return paths, np.asarray(labels)


def topk_ok_scores(features: np.ndarray, labels: np.ndarray, top_k: int) -> np.ndarray:
    ok_features = features[labels == "ok"]
    similarities = features @ ok_features.T
    scores: list[float] = []
    for i in range(features.shape[0]):
        row = similarities[i].copy()
        if labels[i] == "ok":
            ok_indices = np.where(labels == "ok")[0]
            own_ok_position = np.where(ok_indices == i)[0]
            if own_ok_position.size:
                row[own_ok_position[0]] = -np.inf
        finite = row[np.isfinite(row)]
        if finite.size == 0:
            scores.append(1.0)
            continue
        k = min(top_k, finite.size)
        scores.append(float(np.sort(finite)[-k:].mean()))
    return np.asarray(scores, dtype=np.float32)


def fit_pca_distribution(ok_features: np.ndarray, requested_components: int | None) -> tuple[PCA, np.ndarray]:
    max_components = min(ok_features.shape[0] - 1, ok_features.shape[1])
    if max_components < 1:
        raise ValueError("At least two OK samples are required for PCA Mahalanobis scoring.")
    n_components = requested_components or max_components
    n_components = max(1, min(n_components, max_components))
    pca = PCA(n_components=n_components, random_state=7)
    pca.fit(ok_features)
    eigenvalues = np.maximum(pca.explained_variance_, 1e-8)
    return pca, eigenvalues


def pca_mahalanobis_scores(features: np.ndarray, pca: PCA, eigenvalues: np.ndarray) -> np.ndarray:
    projected = pca.transform(features)
    return np.sum((projected * projected) / eigenvalues, axis=1)


def pca_residual_scores(features: np.ndarray, pca: PCA) -> np.ndarray:
    projected = pca.transform(features)
    reconstructed = pca.inverse_transform(projected)
    return np.linalg.norm(features - reconstructed, axis=1)


def ledoit_mahalanobis_scores(features: np.ndarray, ok_features: np.ndarray) -> np.ndarray:
    covariance = LedoitWolf().fit(ok_features)
    return covariance.mahalanobis(features)


def percentile_threshold(values: np.ndarray, labels: np.ndarray, percentile: float) -> float:
    ok_values = values[labels == "ok"]
    return float(np.percentile(ok_values, percentile))


def summarize_metric(name: str, values: np.ndarray, labels: np.ndarray, greater_is_anomaly: bool = True) -> dict[str, str]:
    ok_values = values[labels == "ok"]
    ng_values = values[labels == "ng"]
    if ng_values.size == 0:
        separation = np.nan
    elif greater_is_anomaly:
        separation = float(ng_values.mean() - ok_values.mean())
    else:
        separation = float(ok_values.mean() - ng_values.mean())
    return {
        "metric": name,
        "ok_mean": f"{ok_values.mean():.6f}",
        "ok_min": f"{ok_values.min():.6f}",
        "ok_max": f"{ok_values.max():.6f}",
        "ng_mean": "" if ng_values.size == 0 else f"{ng_values.mean():.6f}",
        "ng_min": "" if ng_values.size == 0 else f"{ng_values.min():.6f}",
        "ng_max": "" if ng_values.size == 0 else f"{ng_values.max():.6f}",
        "mean_separation": "" if np.isnan(separation) else f"{separation:.6f}",
    }


def write_scores(
    image_paths: list[Path],
    labels: np.ndarray,
    metrics: dict[str, np.ndarray],
    thresholds: dict[str, float],
    output_path: Path,
) -> None:
    fieldnames = ["image", "path", "label", *metrics.keys(), *[f"{name}_threshold" for name in thresholds]]
    with output_path.open("w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for i, path in enumerate(image_paths):
            row = {
                "image": path.name,
                "path": str(path.resolve()),
                "label": labels[i],
            }
            row.update({name: f"{values[i]:.8f}" for name, values in metrics.items()})
            row.update({f"{name}_threshold": f"{value:.8f}" for name, value in thresholds.items()})
            writer.writerow(row)


def write_summary(
    metrics: dict[str, np.ndarray],
    labels: np.ndarray,
    thresholds: dict[str, float],
    output_path: Path,
) -> None:
    with output_path.open("w", newline="", encoding="utf-8-sig") as f:
        fieldnames = ["metric", "ok_mean", "ok_min", "ok_max", "ng_mean", "ng_min", "ng_max", "mean_separation", "threshold_p99"]
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for name, values in metrics.items():
            row = summarize_metric(name, values, labels, greater_is_anomaly=name != "ok_topk_similarity")
            row["threshold_p99"] = f"{thresholds[name]:.6f}"
            writer.writerow(row)


def plot_score_histograms(metrics: dict[str, np.ndarray], labels: np.ndarray, output_path: Path) -> None:
    count = len(metrics)
    fig, axes = plt.subplots(count, 1, figsize=(9, max(3, 2.7 * count)))
    if count == 1:
        axes = [axes]
    for ax, (name, values) in zip(axes, metrics.items()):
        ok_values = values[labels == "ok"]
        ng_values = values[labels == "ng"]
        ax.hist(ok_values, bins=min(8, max(3, ok_values.size)), alpha=0.7, label="ok")
        if ng_values.size:
            ax.hist(ng_values, bins=min(8, max(3, ng_values.size)), alpha=0.7, label="ng")
        ax.set_title(name)
        ax.legend()
        ax.grid(True, alpha=0.25)
    fig.tight_layout()
    fig.savefig(output_path, dpi=180)
    plt.close(fig)


def plot_score_scatter(metrics: dict[str, np.ndarray], labels: np.ndarray, output_path: Path) -> None:
    if "pca_mahalanobis" not in metrics or "ok_topk_similarity" not in metrics:
        return
    label_ids = np.asarray([0 if label == "ok" else 1 for label in labels])
    fig, ax = plt.subplots(figsize=(8, 6))
    scatter = ax.scatter(
        metrics["ok_topk_similarity"],
        metrics["pca_mahalanobis"],
        c=label_ids,
        cmap="tab10",
        s=80,
        edgecolors="black",
    )
    ax.set_xlabel("OK TopK Similarity")
    ax.set_ylabel("PCA Mahalanobis")
    ax.set_title("TopK Similarity vs PCA Mahalanobis")
    ax.grid(True, alpha=0.25)
    handles, _ = scatter.legend_elements()
    ax.legend(handles, ["ok", "ng"], title="Label")
    fig.tight_layout()
    fig.savefig(output_path, dpi=180)
    plt.close(fig)


def run(args: argparse.Namespace) -> None:
    image_paths, labels = collect_labeled_images(args.image_root)
    output_dir = args.out_dir
    output_dir.mkdir(parents=True, exist_ok=True)

    features = encode_features(args, image_paths)
    features = l2_normalize(features.astype(np.float32))
    ok_features = features[labels == "ok"]
    if ok_features.shape[0] < 2:
        raise ValueError("At least two OK samples are required.")

    pca, eigenvalues = fit_pca_distribution(ok_features, args.pca_components)
    metrics = {
        "ok_topk_similarity": topk_ok_scores(features, labels, args.top_k),
        "pca_mahalanobis": pca_mahalanobis_scores(features, pca, eigenvalues),
        "pca_residual": pca_residual_scores(features, pca),
        "ledoit_mahalanobis": ledoit_mahalanobis_scores(features, ok_features),
    }
    thresholds = {
        name: percentile_threshold(values if name != "ok_topk_similarity" else -values, labels, args.ok_percentile)
        for name, values in metrics.items()
    }
    thresholds["ok_topk_similarity"] = -thresholds["ok_topk_similarity"]

    prefix = args.encoder if args.encoder != "resnet18" else f"{args.encoder}_{args.resnet_pooling}"
    np.save(output_dir / f"{prefix}_features.npy", features)
    write_scores(image_paths, labels, metrics, thresholds, output_dir / f"{prefix}_distribution_scores.csv")
    write_summary(metrics, labels, thresholds, output_dir / f"{prefix}_distribution_summary.csv")
    plot_score_histograms(metrics, labels, output_dir / f"{prefix}_distribution_histograms.png")
    plot_score_scatter(metrics, labels, output_dir / f"{prefix}_topk_vs_mahalanobis.png")

    print(f"Images: {len(image_paths)}")
    print(f"OK: {(labels == 'ok').sum()}, NG: {(labels == 'ng').sum()}")
    print(f"Encoder: {args.encoder}")
    if args.encoder == "resnet18":
        print(f"ResNet pooling: {args.resnet_pooling}")
    print(f"Feature shape: {features.shape}")
    print(f"PCA components: {pca.n_components_}")
    print(f"Saved outputs: {output_dir}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Score labeled image folders with OK-only feature distribution models.")
    parser.add_argument("--image-root", type=Path, required=True)
    parser.add_argument("--out-dir", type=Path, default=Path("python_demo/outputs/feature_distribution"))
    parser.add_argument("--encoder", default="clip", choices=["clip", "resnet18"])
    parser.add_argument("--resnet-pooling", default="avg", choices=["avg", "max", "avgmax"])
    parser.add_argument("--model-name", default="ViT-B-32")
    parser.add_argument("--pretrained", default="laion2b_s34b_b79k")
    parser.add_argument("--device", default="auto")
    parser.add_argument("--batch-size", type=int, default=16)
    parser.add_argument("--clip-effective-size", type=int, default=224)
    parser.add_argument("--top-k", type=int, default=3)
    parser.add_argument("--pca-components", type=int)
    parser.add_argument("--ok-percentile", type=float, default=99.0)
    run(parser.parse_args())


if __name__ == "__main__":
    main()
