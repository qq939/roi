from __future__ import annotations

import argparse
import csv
import math
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import torch
from torchvision import models, transforms
from torchvision.transforms import InterpolationMode
from PIL import Image, ImageDraw
from scipy.cluster.hierarchy import dendrogram, linkage
from scipy.spatial.distance import squareform
from sklearn.cluster import AgglomerativeClustering
from sklearn.decomposition import PCA
from sklearn.metrics import silhouette_score

from clip_common import ClipConfig, encode_images, list_images, load_clip


CLIP_IMAGE_SIZE = 224
CLIP_MEAN = (0.48145466, 0.4578275, 0.40821073)
CLIP_STD = (0.26862954, 0.26130258, 0.27577711)


def resolve_device(device: str) -> torch.device:
    if device == "auto":
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")
    return torch.device(device)


def forward_resnet18_layer4(model: torch.nn.Module, tensor: torch.Tensor) -> torch.Tensor:
    x = model.conv1(tensor)
    x = model.bn1(x)
    x = model.relu(x)
    x = model.maxpool(x)
    x = model.layer1(x)
    x = model.layer2(x)
    x = model.layer3(x)
    return model.layer4(x)


@torch.inference_mode()
def encode_resnet18(image_paths: list[Path], device_name: str, batch_size: int, pooling: str) -> np.ndarray:
    device = resolve_device(device_name)
    weights = models.ResNet18_Weights.DEFAULT
    model = models.resnet18(weights=weights)
    model.eval().to(device)
    preprocess = weights.transforms()

    features: list[np.ndarray] = []
    for start in range(0, len(image_paths), batch_size):
        batch_paths = image_paths[start : start + batch_size]
        images = []
        for image_path in batch_paths:
            with Image.open(image_path) as image:
                images.append(preprocess(image.convert("RGB")))
        tensor = torch.stack(images).to(device)
        feature_map = forward_resnet18_layer4(model, tensor)
        if pooling == "avg":
            image_features = torch.nn.functional.adaptive_avg_pool2d(feature_map, output_size=1).flatten(1)
        elif pooling == "max":
            image_features = torch.nn.functional.adaptive_max_pool2d(feature_map, output_size=1).flatten(1)
        elif pooling == "avgmax":
            avg_features = torch.nn.functional.adaptive_avg_pool2d(feature_map, output_size=1).flatten(1)
            max_features = torch.nn.functional.adaptive_max_pool2d(feature_map, output_size=1).flatten(1)
            image_features = torch.cat([avg_features, max_features], dim=1)
        else:
            raise ValueError(f"Unsupported ResNet pooling: {pooling}")
        image_features = image_features / image_features.norm(dim=-1, keepdim=True).clamp_min(1e-12)
        features.append(image_features.detach().cpu().float().numpy())
    return np.concatenate(features, axis=0)


def encode_features(args: argparse.Namespace, image_paths: list[Path]) -> np.ndarray:
    if args.encoder == "clip":
        model, preprocess, _, device = load_clip(ClipConfig(args.model_name, args.pretrained, args.device))
        effective_size = getattr(args, "clip_effective_size", None)
        if effective_size is not None and effective_size != CLIP_IMAGE_SIZE:
            preprocess = transforms.Compose(
                [
                    transforms.Resize(CLIP_IMAGE_SIZE, interpolation=InterpolationMode.BICUBIC),
                    transforms.CenterCrop(CLIP_IMAGE_SIZE),
                    transforms.Resize((effective_size, effective_size), interpolation=InterpolationMode.BICUBIC),
                    transforms.Resize((CLIP_IMAGE_SIZE, CLIP_IMAGE_SIZE), interpolation=InterpolationMode.BICUBIC),
                    transforms.ToTensor(),
                    transforms.Normalize(mean=CLIP_MEAN, std=CLIP_STD),
                ]
            )
        return encode_images(model, preprocess, device, image_paths, args.batch_size)
    if args.encoder == "resnet18":
        return encode_resnet18(image_paths, args.device, args.batch_size, args.resnet_pooling)
    raise ValueError(f"Unsupported encoder: {args.encoder}")


def cosine_distance_matrix(features: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    similarities = np.clip(features @ features.T, -1.0, 1.0)
    distances = np.clip(1.0 - similarities, 0.0, 2.0)
    np.fill_diagonal(distances, 0.0)
    return similarities, distances


def choose_cluster_count(distances: np.ndarray, max_clusters: int) -> tuple[int, dict[int, float]]:
    sample_count = distances.shape[0]
    if sample_count < 3:
        return 1, {}

    scores: dict[int, float] = {}
    upper = min(max_clusters, sample_count - 1)
    for k in range(2, upper + 1):
        labels = AgglomerativeClustering(
            n_clusters=k,
            metric="precomputed",
            linkage="average",
        ).fit_predict(distances)
        if len(set(labels)) < 2:
            continue
        scores[k] = float(silhouette_score(distances, labels, metric="precomputed"))

    if not scores:
        return 1, {}
    best_k = max(scores, key=scores.get)
    return best_k, scores


def cluster_features(distances: np.ndarray, cluster_count: int) -> np.ndarray:
    if cluster_count <= 1:
        return np.zeros(distances.shape[0], dtype=int)
    return AgglomerativeClustering(
        n_clusters=cluster_count,
        metric="precomputed",
        linkage="average",
    ).fit_predict(distances)


def project_pca(features: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    components = min(3, features.shape[0], features.shape[1])
    if components < 2:
        return np.zeros((features.shape[0], 3), dtype=np.float32), np.zeros(3, dtype=np.float32)
    pca = PCA(n_components=components, random_state=7)
    projected = pca.fit_transform(features)
    if components < 3:
        projected = np.pad(projected, ((0, 0), (0, 3 - components)))
    return projected, pca.explained_variance_ratio_


def write_similarity_csv(image_paths: list[Path], similarities: np.ndarray, output_path: Path) -> None:
    with output_path.open("w", newline="", encoding="utf-8-sig") as f:
        writer = csv.writer(f)
        writer.writerow(["image", *[path.name for path in image_paths]])
        for path, row in zip(image_paths, similarities):
            writer.writerow([path.name, *[f"{value:.6f}" for value in row]])


def write_feature_summary(
    image_paths: list[Path],
    labels: np.ndarray,
    similarities: np.ndarray,
    pca_xy: np.ndarray,
    output_path: Path,
) -> None:
    with output_path.open("w", newline="", encoding="utf-8-sig") as f:
        fieldnames = [
            "image",
            "path",
            "folder_label",
            "cluster",
            "pca_x",
            "pca_y",
            "nearest_image",
            "nearest_similarity",
            "mean_top3_similarity",
            "mean_similarity",
            "outlier_score",
        ]
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for i, path in enumerate(image_paths):
            sims = similarities[i].copy()
            sims[i] = -np.inf
            order = np.argsort(-sims)
            valid = order[np.isfinite(sims[order])]
            top3 = valid[: min(3, valid.size)]
            nearest = valid[0] if valid.size else i
            mean_top3 = float(np.mean(sims[top3])) if top3.size else 1.0
            finite_sims = sims[np.isfinite(sims)]
            mean_similarity = float(np.mean(finite_sims)) if finite_sims.size else 1.0
            writer.writerow(
                {
                    "image": path.name,
                    "path": str(path.resolve()),
                    "folder_label": path.parent.name,
                    "cluster": int(labels[i]),
                    "pca_x": f"{pca_xy[i, 0]:.6f}",
                    "pca_y": f"{pca_xy[i, 1]:.6f}",
                    "nearest_image": image_paths[nearest].name,
                    "nearest_similarity": f"{float(sims[nearest]):.6f}",
                    "mean_top3_similarity": f"{mean_top3:.6f}",
                    "mean_similarity": f"{mean_similarity:.6f}",
                    "outlier_score": f"{1.0 - mean_top3:.6f}",
                }
            )


def folder_labels(image_paths: list[Path]) -> tuple[list[str], np.ndarray]:
    names = [path.parent.name for path in image_paths]
    unique = {name: index for index, name in enumerate(sorted(set(names)))}
    return names, np.asarray([unique[name] for name in names], dtype=int)


def write_cluster_summary(
    image_paths: list[Path],
    labels: np.ndarray,
    similarities: np.ndarray,
    silhouette_scores: dict[int, float],
    selected_k: int,
    output_path: Path,
) -> None:
    with output_path.open("w", newline="", encoding="utf-8-sig") as f:
        fieldnames = [
            "cluster",
            "count",
            "mean_intra_similarity",
            "images",
            "selected_cluster_count",
            "silhouette_scores",
        ]
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        score_text = "; ".join(f"k={k}:{v:.4f}" for k, v in sorted(silhouette_scores.items()))
        for label in sorted(set(labels.tolist())):
            indices = np.where(labels == label)[0]
            if indices.size > 1:
                sub = similarities[np.ix_(indices, indices)].copy()
                sub[np.eye(indices.size, dtype=bool)] = np.nan
                mean_intra = float(np.nanmean(sub))
            else:
                mean_intra = 1.0
            writer.writerow(
                {
                    "cluster": int(label),
                    "count": int(indices.size),
                    "mean_intra_similarity": f"{mean_intra:.6f}",
                    "images": " | ".join(image_paths[i].name for i in indices),
                    "selected_cluster_count": selected_k,
                    "silhouette_scores": score_text,
                }
            )


def plot_similarity_heatmap(image_paths: list[Path], similarities: np.ndarray, output_path: Path) -> None:
    size = max(6, min(14, len(image_paths) * 0.65))
    fig, ax = plt.subplots(figsize=(size, size))
    im = ax.imshow(similarities, vmin=0.0, vmax=1.0, cmap="viridis")
    labels = [path.stem[-18:] for path in image_paths]
    ax.set_xticks(range(len(labels)))
    ax.set_yticks(range(len(labels)))
    ax.set_xticklabels(labels, rotation=75, ha="right", fontsize=8)
    ax.set_yticklabels(labels, fontsize=8)
    ax.set_title("CLIP Cosine Similarity")
    fig.colorbar(im, ax=ax, fraction=0.046, pad=0.04)
    fig.tight_layout()
    fig.savefig(output_path, dpi=180)
    plt.close(fig)


def plot_pca_scatter(
    image_paths: list[Path],
    labels: np.ndarray,
    pca_xy: np.ndarray,
    explained_variance: np.ndarray,
    output_path: Path,
) -> None:
    fig, ax = plt.subplots(figsize=(9, 7))
    scatter = ax.scatter(pca_xy[:, 0], pca_xy[:, 1], c=labels, cmap="tab10", s=90, edgecolors="black")
    for i, path in enumerate(image_paths):
        ax.annotate(str(i + 1), (pca_xy[i, 0], pca_xy[i, 1]), textcoords="offset points", xytext=(6, 5), fontsize=9)
    var_text = ""
    if explained_variance.size >= 2:
        var_text = f" ({explained_variance[0]:.1%}, {explained_variance[1]:.1%})"
    ax.set_title(f"CLIP Feature PCA{var_text}")
    ax.set_xlabel("PC1")
    ax.set_ylabel("PC2")
    ax.grid(True, alpha=0.25)
    legend = ax.legend(*scatter.legend_elements(), title="Cluster", loc="best")
    ax.add_artist(legend)
    fig.tight_layout()
    fig.savefig(output_path, dpi=180)
    plt.close(fig)


def plot_pca_scatter_by_folder(
    image_paths: list[Path],
    pca_xy: np.ndarray,
    explained_variance: np.ndarray,
    output_path: Path,
) -> None:
    names, label_ids = folder_labels(image_paths)
    fig, ax = plt.subplots(figsize=(9, 7))
    scatter = ax.scatter(pca_xy[:, 0], pca_xy[:, 1], c=label_ids, cmap="tab10", s=90, edgecolors="black")
    for i, path in enumerate(image_paths):
        ax.annotate(str(i + 1), (pca_xy[i, 0], pca_xy[i, 1]), textcoords="offset points", xytext=(6, 5), fontsize=9)
    var_text = ""
    if explained_variance.size >= 2:
        var_text = f" ({explained_variance[0]:.1%}, {explained_variance[1]:.1%})"
    ax.set_title(f"Feature PCA by Folder Label{var_text}")
    ax.set_xlabel("PC1")
    ax.set_ylabel("PC2")
    ax.grid(True, alpha=0.25)
    handles, _ = scatter.legend_elements()
    unique_names = sorted(set(names))
    ax.legend(handles, unique_names, title="Folder", loc="best")
    fig.tight_layout()
    fig.savefig(output_path, dpi=180)
    plt.close(fig)


def plot_pca_3d_static(
    image_paths: list[Path],
    labels: np.ndarray,
    pca_xyz: np.ndarray,
    explained_variance: np.ndarray,
    output_path: Path,
) -> None:
    fig = plt.figure(figsize=(9, 7))
    ax = fig.add_subplot(111, projection="3d")
    ax.scatter(
        pca_xyz[:, 0],
        pca_xyz[:, 1],
        pca_xyz[:, 2],
        c=labels,
        cmap="tab10",
        s=90,
        edgecolors="black",
        depthshade=True,
    )
    for i, _ in enumerate(image_paths):
        ax.text(pca_xyz[i, 0], pca_xyz[i, 1], pca_xyz[i, 2], str(i + 1), fontsize=9)
    var = list(explained_variance[:3]) + [0.0] * max(0, 3 - explained_variance.size)
    ax.set_title(f"CLIP Feature PCA 3D ({var[0]:.1%}, {var[1]:.1%}, {var[2]:.1%})")
    ax.set_xlabel("PC1")
    ax.set_ylabel("PC2")
    ax.set_zlabel("PC3")
    fig.tight_layout()
    fig.savefig(output_path, dpi=180)
    plt.close(fig)


def plot_pca_3d_html(
    image_paths: list[Path],
    labels: np.ndarray,
    pca_xyz: np.ndarray,
    explained_variance: np.ndarray,
    output_path: Path,
) -> bool:
    try:
        import plotly.graph_objects as go
    except ImportError:
        return False

    var = list(explained_variance[:3]) + [0.0] * max(0, 3 - explained_variance.size)
    hover_text = [
        f"#{i + 1}<br>{path.name}<br>cluster={int(labels[i])}<br>"
        f"PC1={pca_xyz[i, 0]:.4f}<br>PC2={pca_xyz[i, 1]:.4f}<br>PC3={pca_xyz[i, 2]:.4f}"
        for i, path in enumerate(image_paths)
    ]
    fig = go.Figure(
        data=[
            go.Scatter3d(
                x=pca_xyz[:, 0],
                y=pca_xyz[:, 1],
                z=pca_xyz[:, 2],
                mode="markers+text",
                text=[str(i + 1) for i in range(len(image_paths))],
                hovertext=hover_text,
                hoverinfo="text",
                marker={
                    "size": 7,
                    "color": labels,
                    "colorscale": "Viridis",
                    "line": {"width": 1, "color": "black"},
                },
            )
        ]
    )
    fig.update_layout(
        title=f"CLIP Feature PCA 3D ({var[0]:.1%}, {var[1]:.1%}, {var[2]:.1%})",
        scene={
            "xaxis_title": "PC1",
            "yaxis_title": "PC2",
            "zaxis_title": "PC3",
        },
        margin={"l": 0, "r": 0, "t": 50, "b": 0},
    )
    fig.write_html(output_path, include_plotlyjs="cdn")
    return True


def plot_pca_3d_html_by_folder(
    image_paths: list[Path],
    pca_xyz: np.ndarray,
    explained_variance: np.ndarray,
    output_path: Path,
) -> bool:
    try:
        import plotly.graph_objects as go
    except ImportError:
        return False

    names, label_ids = folder_labels(image_paths)
    var = list(explained_variance[:3]) + [0.0] * max(0, 3 - explained_variance.size)
    hover_text = [
        f"#{i + 1}<br>{path.name}<br>folder={names[i]}<br>"
        f"PC1={pca_xyz[i, 0]:.4f}<br>PC2={pca_xyz[i, 1]:.4f}<br>PC3={pca_xyz[i, 2]:.4f}"
        for i, path in enumerate(image_paths)
    ]
    fig = go.Figure(
        data=[
            go.Scatter3d(
                x=pca_xyz[:, 0],
                y=pca_xyz[:, 1],
                z=pca_xyz[:, 2],
                mode="markers+text",
                text=[str(i + 1) for i in range(len(image_paths))],
                hovertext=hover_text,
                hoverinfo="text",
                marker={
                    "size": 7,
                    "color": label_ids,
                    "colorscale": "Viridis",
                    "line": {"width": 1, "color": "black"},
                },
            )
        ]
    )
    fig.update_layout(
        title=f"Feature PCA 3D by Folder Label ({var[0]:.1%}, {var[1]:.1%}, {var[2]:.1%})",
        scene={
            "xaxis_title": "PC1",
            "yaxis_title": "PC2",
            "zaxis_title": "PC3",
        },
        margin={"l": 0, "r": 0, "t": 50, "b": 0},
    )
    fig.write_html(output_path, include_plotlyjs="cdn")
    return True


def plot_dendrogram(image_paths: list[Path], distances: np.ndarray, output_path: Path) -> None:
    if len(image_paths) < 2:
        return
    condensed = squareform(distances, checks=False)
    linked = linkage(condensed, method="average")
    fig, ax = plt.subplots(figsize=(11, 6))
    labels = [f"{i + 1}: {path.stem[-18:]}" for i, path in enumerate(image_paths)]
    dendrogram(linked, labels=labels, leaf_rotation=75, leaf_font_size=8, ax=ax)
    ax.set_title("CLIP Feature Hierarchical Clustering")
    ax.set_ylabel("Cosine Distance")
    fig.tight_layout()
    fig.savefig(output_path, dpi=180)
    plt.close(fig)


def make_contact_sheet(image_paths: list[Path], labels: np.ndarray, output_path: Path) -> None:
    order = sorted(range(len(image_paths)), key=lambda i: (int(labels[i]), image_paths[i].name))
    thumb_w, thumb_h = 150, 120
    label_h = 38
    cols = min(5, max(1, math.ceil(math.sqrt(len(order)))))
    rows = math.ceil(len(order) / cols)
    sheet = Image.new("RGB", (cols * thumb_w, rows * (thumb_h + label_h)), "white")
    draw = ImageDraw.Draw(sheet)
    for position, index in enumerate(order):
        col = position % cols
        row = position // cols
        x = col * thumb_w
        y = row * (thumb_h + label_h)
        with Image.open(image_paths[index]) as image:
            image = image.convert("RGB")
            image.thumbnail((thumb_w, thumb_h))
            sheet.paste(image, (x + (thumb_w - image.width) // 2, y))
        draw.text((x + 4, y + thumb_h + 3), f"#{index + 1}  cluster={int(labels[index])}", fill=(0, 0, 0))
        draw.text((x + 4, y + thumb_h + 19), image_paths[index].stem[:22], fill=(40, 40, 40))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output_path, quality=92)


def run(args: argparse.Namespace) -> None:
    image_paths = list_images(args.image_dir)
    if not image_paths:
        raise ValueError(f"No images found under {args.image_dir}")

    output_dir = args.out_dir
    output_dir.mkdir(parents=True, exist_ok=True)

    features = encode_features(args, image_paths)
    similarities, distances = cosine_distance_matrix(features)
    selected_k, silhouette_scores = choose_cluster_count(distances, args.max_clusters)
    labels = cluster_features(distances, selected_k)
    pca_xy, explained_variance = project_pca(features)

    prefix = args.encoder if args.encoder != "resnet18" else f"{args.encoder}_{args.resnet_pooling}"
    np.save(output_dir / f"{prefix}_features.npy", features)
    write_feature_summary(image_paths, labels, similarities, pca_xy, output_dir / f"{prefix}_feature_summary.csv")
    write_cluster_summary(
        image_paths,
        labels,
        similarities,
        silhouette_scores,
        selected_k,
        output_dir / f"{prefix}_cluster_summary.csv",
    )
    write_similarity_csv(image_paths, similarities, output_dir / f"{prefix}_similarity_matrix.csv")
    plot_similarity_heatmap(image_paths, similarities, output_dir / f"{prefix}_similarity_heatmap.png")
    plot_pca_scatter(image_paths, labels, pca_xy, explained_variance, output_dir / f"{prefix}_pca_scatter.png")
    plot_pca_scatter_by_folder(image_paths, pca_xy, explained_variance, output_dir / f"{prefix}_pca_scatter_by_folder.png")
    plot_pca_3d_static(image_paths, labels, pca_xy, explained_variance, output_dir / f"{prefix}_pca_3d.png")
    wrote_3d_html = plot_pca_3d_html(image_paths, labels, pca_xy, explained_variance, output_dir / f"{prefix}_pca_3d.html")
    wrote_folder_3d_html = plot_pca_3d_html_by_folder(
        image_paths,
        pca_xy,
        explained_variance,
        output_dir / f"{prefix}_pca_3d_by_folder.html",
    )
    plot_dendrogram(image_paths, distances, output_dir / f"{prefix}_dendrogram.png")
    make_contact_sheet(image_paths, labels, output_dir / f"{prefix}_cluster_contact_sheet.jpg")

    print(f"Images: {len(image_paths)}")
    print(f"Encoder: {args.encoder}")
    print(f"Feature shape: {features.shape}")
    print(f"Selected clusters: {selected_k}")
    if silhouette_scores:
        print("Silhouette:", ", ".join(f"k={k}:{v:.4f}" for k, v in sorted(silhouette_scores.items())))
    print(f"Interactive 3D HTML: {'yes' if wrote_3d_html else 'no (plotly not installed)'}")
    print(f"Folder-label 3D HTML: {'yes' if wrote_folder_3d_html else 'no (plotly not installed)'}")
    print(f"Saved outputs: {output_dir}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Cluster image crops with global image features.")
    parser.add_argument("--image-dir", type=Path, required=True)
    parser.add_argument("--out-dir", type=Path, default=Path("python_demo/outputs/clip_cluster"))
    parser.add_argument("--encoder", default="clip", choices=["clip", "resnet18"])
    parser.add_argument("--resnet-pooling", default="avg", choices=["avg", "max", "avgmax"])
    parser.add_argument("--model-name", default="ViT-B-32")
    parser.add_argument("--pretrained", default="laion2b_s34b_b79k")
    parser.add_argument("--device", default="auto")
    parser.add_argument("--batch-size", type=int, default=16)
    parser.add_argument("--clip-effective-size", type=int, default=CLIP_IMAGE_SIZE)
    parser.add_argument("--max-clusters", type=int, default=6)
    run(parser.parse_args())


if __name__ == "__main__":
    main()
