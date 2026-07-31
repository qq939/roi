from __future__ import annotations

import argparse
from pathlib import Path

import torch

from clip_common import ClipConfig, load_clip


class NormalizedImageEncoder(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, images):
        features = self.model.encode_image(images)
        return features / features.norm(dim=-1, keepdim=True).clamp_min(1e-12)


def export_image_encoder(
    out: Path,
    model_name: str,
    pretrained: str,
    opset: int,
) -> None:
    model, _, _, _ = load_clip(ClipConfig(model_name=model_name, pretrained=pretrained, device="cpu"))
    wrapper = NormalizedImageEncoder(model).eval()
    dummy = torch.randn(1, 3, 224, 224, dtype=torch.float32)

    out.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        wrapper,
        dummy,
        str(out),
        input_names=["images"],
        output_names=["image_features"],
        dynamic_axes={
            "images": {0: "batch"},
            "image_features": {0: "batch"},
        },
        opset_version=opset,
        do_constant_folding=True,
    )
    print(f"Exported image encoder: {out.resolve()}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Export OpenCLIP image encoder to ONNX.")
    parser.add_argument("--out", type=Path, default=Path("Models/clip_vit_b32_image.onnx"))
    parser.add_argument("--model", default="ViT-B-32")
    parser.add_argument("--pretrained", default="laion2b_s34b_b79k")
    parser.add_argument("--opset", type=int, default=17)
    args = parser.parse_args()

    export_image_encoder(args.out, args.model, args.pretrained, args.opset)


if __name__ == "__main__":
    main()
