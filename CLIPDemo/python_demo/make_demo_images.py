from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw


def make_canvas() -> Image.Image:
    return Image.new("RGB", (320, 320), (238, 238, 232))


def draw_part(
    path: Path,
    body_color: tuple[int, int, int],
    connector_color: tuple[int, int, int],
    rotation: str = "normal",
    missing_connector: bool = False,
) -> None:
    image = make_canvas()
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle((70, 90, 250, 230), radius=16, fill=body_color, outline=(25, 25, 25), width=4)
    draw.ellipse((105, 125, 145, 165), fill=(30, 30, 30))
    draw.ellipse((175, 125, 215, 165), fill=(30, 30, 30))
    draw.rectangle((128, 185, 192, 202), fill=(70, 70, 70))

    if not missing_connector:
        if rotation == "normal":
            draw.rectangle((250, 138, 292, 182), fill=connector_color, outline=(25, 25, 25), width=3)
            draw.line((260, 148, 282, 172), fill=(255, 255, 255), width=3)
        else:
            draw.rectangle((28, 138, 70, 182), fill=connector_color, outline=(25, 25, 25), width=3)
            draw.line((38, 172, 60, 148), fill=(255, 255, 255), width=3)

    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path)


def create_ok_cache_samples(root: Path) -> None:
    ok_dir = root / "part_A" / "ok"
    test_dir = root / "part_A" / "test"

    variants = [
        ((86, 142, 210), (225, 145, 46), "normal", False),
        ((80, 135, 204), (235, 154, 50), "normal", False),
        ((92, 148, 216), (218, 138, 42), "normal", False),
        ((76, 130, 196), (228, 148, 44), "normal", False),
    ]
    for index, args in enumerate(variants, start=1):
        draw_part(ok_dir / f"ok_{index:02d}.png", *args)

    draw_part(test_dir / "ok_like.png", (88, 140, 208), (230, 150, 48), "normal", False)
    draw_part(test_dir / "missing_connector.png", (88, 140, 208), (230, 150, 48), "normal", True)
    draw_part(test_dir / "reversed_connector.png", (88, 140, 208), (230, 150, 48), "reversed", False)


def create_tip_adapter_samples(root: Path) -> None:
    train_root = root / "tip_adapter" / "train"
    test_root = root / "tip_adapter" / "test"

    classes = {
        "blue_part": ((86, 142, 210), (225, 145, 46)),
        "green_part": ((72, 164, 120), (80, 116, 210)),
    }

    for class_name, colors in classes.items():
        for index in range(1, 4):
            body = tuple(max(0, min(255, channel + (index - 2) * 8)) for channel in colors[0])
            draw_part(train_root / class_name / f"{class_name}_{index:02d}.png", body, colors[1], "normal", False)

    draw_part(test_root / "blue_query.png", (89, 143, 212), (228, 148, 47), "normal", False)
    draw_part(test_root / "green_query.png", (76, 166, 124), (78, 114, 214), "normal", False)


def main() -> None:
    parser = argparse.ArgumentParser(description="Create tiny synthetic images for CLIP cache demos.")
    parser.add_argument("--out", type=Path, default=Path("Samples"), help="Output sample root.")
    args = parser.parse_args()

    create_ok_cache_samples(args.out)
    create_tip_adapter_samples(args.out)
    print(f"Demo images written to {args.out.resolve()}")


if __name__ == "__main__":
    main()
