#!/usr/bin/env python3
"""Split the generated 4x4 transparent pose sheets into normalized WPF sprites."""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy.ndimage import find_objects, label


ROOT = Path(__file__).resolve().parents[1]
SHEETS = ROOT / "Artwork" / "TransparentSheets"
OUTPUT = ROOT / "BunnyCompanion" / "Assets" / "Sprites"
CANVAS_SIZE = 384
CONTENT_SIZE = 360
BASE_SCALE = 1.18

SPRITES = {
    "core_motion.png": [
        "idle", "breathe", "blink", "wave",
        "walk_1", "walk_2", "walk_3", "tiptoe",
        "jump_ready", "jump", "land", "look_back",
        "sit", "kneel", "dragged", "recover",
    ],
    "emotions.png": [
        "delighted", "wink", "heart", "kiss",
        "shy", "bashful", "clap", "celebrate",
        "surprised", "curious", "pout", "annoyed",
        "sad", "laugh", "headpat", "gift",
    ],
    "daily_life.png": [
        "sleepy", "yawn", "drowsy", "sleep_curl",
        "sleep_side", "stretch", "drink", "read",
        "music", "dance", "reminder", "point",
        "birthday", "plush", "rain", "flowers",
    ],
}


def normalize(cell: Image.Image) -> Image.Image:
    # Generated sheets can contain a few pixels from a pose in the next cell.
    # Keep the main connected component and only nearby accessory/effect pieces.
    pixels = np.array(cell)
    mask = pixels[:, :, 3] >= 10
    labels, count = label(mask)
    components = find_objects(labels)
    if count:
        sizes = [int((labels[item] == index).sum()) if item else 0
                 for index, item in enumerate(components, start=1)]
        main_index = int(np.argmax(sizes)) + 1
        main_slice = components[main_index - 1]
        if main_slice is not None:
            main_y, main_x = main_slice
            margin = 8
            keep = np.zeros_like(mask)
            for index, item in enumerate(components, start=1):
                if item is None:
                    continue
                part_y, part_x = item
                intersects = not (
                    part_x.stop < main_x.start - margin
                    or part_x.start > main_x.stop + margin
                    or part_y.stop < main_y.start - margin
                    or part_y.start > main_y.stop + margin
                )
                if intersects:
                    keep |= labels == index
            pixels[~keep, 3] = 0
            cell = Image.fromarray(pixels, "RGBA")

    alpha = cell.getchannel("A")
    # Ignore almost-transparent antialiasing specks when finding the content box.
    bbox = alpha.point(lambda a: 255 if a >= 10 else 0).getbbox()
    if bbox is None:
        raise ValueError("Sprite cell contains no visible pixels")
    cropped = cell.crop(bbox)
    # Keep one shared pixel scale across standing, sitting and reclining poses.
    # Per-pose normalization makes short poses grow unnaturally during transitions.
    scale = min(BASE_SCALE, CONTENT_SIZE / cropped.width, CONTENT_SIZE / cropped.height)
    size = (max(1, round(cropped.width * scale)), max(1, round(cropped.height * scale)))
    resized = cropped.resize(size, Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    x = (CANVAS_SIZE - resized.width) // 2
    # Keep the visual baseline stable while leaving room above hair and effects.
    y = CANVAS_SIZE - resized.height - 12
    if y < 6:
        y = 6
    canvas.alpha_composite(resized, (x, y))
    return canvas


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    manifest: dict[str, dict[str, object]] = {}
    for sheet_name, names in SPRITES.items():
        sheet = Image.open(SHEETS / sheet_name).convert("RGBA")
        width, height = sheet.size
        for index, name in enumerate(names):
            row, col = divmod(index, 4)
            left = round(col * width / 4)
            right = round((col + 1) * width / 4)
            top = round(row * height / 4)
            bottom = round((row + 1) * height / 4)
            sprite = normalize(sheet.crop((left, top, right, bottom)))
            output_path = OUTPUT / f"{name}.png"
            sprite.save(output_path, optimize=True)
            manifest[name] = {
                "file": f"Assets/Sprites/{name}.png",
                "sheet": sheet_name,
                "cell": index + 1,
                "size": [CANVAS_SIZE, CANVAS_SIZE],
            }

    with (ROOT / "Artwork" / "sprite_manifest.json").open("w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)

    idle = Image.open(OUTPUT / "idle.png").convert("RGBA")
    icon_canvas = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    icon_draw = ImageDraw.Draw(icon_canvas)
    icon_draw.ellipse((4, 4, 252, 252), fill=(255, 232, 241, 255))
    portrait = idle.crop((90, 15, 294, 219)).resize((224, 224), Image.Resampling.LANCZOS)
    icon_canvas.alpha_composite(portrait, (16, 13))
    icon_canvas.save(
        ROOT / "BunnyCompanion" / "Assets" / "BunnyCompanion.ico",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print(f"Created {len(manifest)} sprites in {OUTPUT}")


if __name__ == "__main__":
    main()
