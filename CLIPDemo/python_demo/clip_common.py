from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np
import open_clip
import torch
from PIL import Image


IMAGE_EXTENSIONS = {".bmp", ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff"}


@dataclass(frozen=True)
class ClipConfig:
    model_name: str = "ViT-B-32"
    pretrained: str = "laion2b_s34b_b79k"
    device: str = "auto"


def resolve_device(device: str) -> torch.device:
    if device == "auto":
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")
    return torch.device(device)


def load_clip(config: ClipConfig):
    device = resolve_device(config.device)
    model, _, preprocess = open_clip.create_model_and_transforms(
        config.model_name,
        pretrained=config.pretrained,
        device=device,
    )
    tokenizer = open_clip.get_tokenizer(config.model_name)
    model.eval()
    return model, preprocess, tokenizer, device


def list_images(path: Path) -> list[Path]:
    if path.is_file() and path.suffix.lower() in IMAGE_EXTENSIONS:
        return [path]
    return sorted(p for p in path.rglob("*") if p.suffix.lower() in IMAGE_EXTENSIONS)


@torch.inference_mode()
def encode_images(model, preprocess, device: torch.device, image_paths: list[Path], batch_size: int = 16) -> np.ndarray:
    features: list[np.ndarray] = []
    for start in range(0, len(image_paths), batch_size):
        batch_paths = image_paths[start : start + batch_size]
        images = []
        for image_path in batch_paths:
            with Image.open(image_path) as image:
                images.append(preprocess(image.convert("RGB")))
        tensor = torch.stack(images).to(device)
        image_features = model.encode_image(tensor)
        image_features = image_features / image_features.norm(dim=-1, keepdim=True)
        features.append(image_features.detach().cpu().float().numpy())
    if not features:
        return np.empty((0, 0), dtype=np.float32)
    return np.concatenate(features, axis=0)


@torch.inference_mode()
def encode_texts(model, tokenizer, device: torch.device, texts: list[str]) -> np.ndarray:
    tokens = tokenizer(texts).to(device)
    text_features = model.encode_text(tokens)
    text_features = text_features / text_features.norm(dim=-1, keepdim=True)
    return text_features.detach().cpu().float().numpy()


def l2_normalize(feature: np.ndarray) -> np.ndarray:
    norm = np.linalg.norm(feature, axis=-1, keepdims=True)
    return feature / np.clip(norm, 1e-12, None)
