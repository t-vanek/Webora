from __future__ import annotations

import csv
import json
import argparse
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageOps


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = ROOT / "artifacts" / "pdfpig-0.png"
DEFAULT_OUTPUT = ROOT / "output" / "pdf"


def enhance(source: Image.Image) -> Image.Image:
    upscaled = source.resize((source.width * 3, source.height * 3), Image.Resampling.LANCZOS)
    upscaled = ImageOps.autocontrast(upscaled, cutoff=1)
    upscaled = ImageEnhance.Contrast(upscaled).enhance(1.18)
    upscaled = ImageEnhance.Sharpness(upscaled).enhance(2.2)
    return upscaled


def green_mask(source: Image.Image, close: bool) -> Image.Image:
    rgb = np.asarray(source.convert("RGB")).astype(np.int16)
    r = rgb[:, :, 0]
    g = rgb[:, :, 1]
    b = rgb[:, :, 2]

    # The highlighted stalls are the only saturated green regions in this scan.
    mask = (g > 120) & (g > r + 28) & (g > b + 18) & ((g - np.minimum(r, b)) > 35)
    img = Image.fromarray((mask.astype(np.uint8) * 255), mode="L")

    if close:
        # Close tiny holes from text without merging distant highlighted areas.
        img = img.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.MinFilter(5))
    return img


def components(mask: Image.Image, raw_mask: Image.Image) -> list[dict[str, int | float]]:
    arr = np.asarray(mask) > 0
    height, width = arr.shape
    seen = np.zeros_like(arr, dtype=bool)
    found: list[dict[str, int | float]] = []

    for y in range(height):
        for x in range(width):
            if not arr[y, x] or seen[y, x]:
                continue

            q: deque[tuple[int, int]] = deque([(x, y)])
            seen[y, x] = True
            xs: list[int] = []
            ys: list[int] = []

            while q:
                cx, cy = q.popleft()
                xs.append(cx)
                ys.append(cy)

                for nx, ny in ((cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1)):
                    if 0 <= nx < width and 0 <= ny < height and arr[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        q.append((nx, ny))

            x1, x2 = min(xs), max(xs)
            y1, y2 = min(ys), max(ys)
            w = x2 - x1 + 1
            h = y2 - y1 + 1
            area = len(xs)

            if area < 80 or w < 8 or h < 8:
                continue

            found.extend(split_component(raw_mask, x1, y1, w, h, area))

    found.sort(key=lambda c: (int(c["y"]), int(c["x"])))
    for index, item in enumerate(found, start=1):
        item["id"] = index
        item["label"] = ""
        item["confidence"] = "geometry_only"
    return found


def split_component(raw_mask: Image.Image, x: int, y: int, w: int, h: int, area: int) -> list[dict[str, int | float]]:
    raw = np.asarray(raw_mask.crop((x, y, x + w, y + h))) > 0

    row_ranges = filled_ranges(raw.sum(axis=1), min_run=max(10, min(28, h // 8)))
    col_ranges = filled_ranges(raw.sum(axis=0), min_run=max(10, min(28, w // 8)))

    if len(row_ranges) > 1 and len(row_ranges) >= len(col_ranges):
        return [
            make_component(x, y + start, w, end - start + 1, int(raw[start : end + 1, :].sum()))
            for start, end in row_ranges
        ]

    if len(col_ranges) > 1:
        return [
            make_component(x + start, y, end - start + 1, h, int(raw[:, start : end + 1].sum()))
            for start, end in col_ranges
        ]

    return [make_component(x, y, w, h, area)]


def filled_ranges(projection: np.ndarray, min_run: int) -> list[tuple[int, int]]:
    if projection.size == 0:
        return []

    threshold = max(2, int(projection.max() * 0.28))
    filled = projection >= threshold
    ranges: list[tuple[int, int]] = []
    start: int | None = None

    for i, has_fill in enumerate(filled):
        if has_fill and start is None:
            start = i
        elif not has_fill and start is not None:
            if i - start >= min_run:
                ranges.append((start, i - 1))
            start = None

    if start is not None and len(filled) - start >= min_run:
        ranges.append((start, len(filled) - 1))

    return ranges


def make_component(x: int, y: int, w: int, h: int, area: int) -> dict[str, int | float]:
    return {
        "x": x,
        "y": y,
        "width": w,
        "height": h,
        "center_x": round(x + w / 2, 1),
        "center_y": round(y + h / 2, 1),
        "area": area,
        "aspect": round(w / h, 3),
    }


def draw_overlay(source: Image.Image, found: list[dict[str, int | float]]) -> Image.Image:
    overlay = source.convert("RGB")
    draw = ImageDraw.Draw(overlay)

    for item in found:
        x = int(item["x"])
        y = int(item["y"])
        w = int(item["width"])
        h = int(item["height"])
        draw.rectangle([x, y, x + w, y + h], outline=(126, 55, 246), width=3)
        draw.text((x + 3, max(0, y - 14)), str(item["id"]), fill=(126, 55, 246))

    return overlay


def contact_sheet(source: Image.Image, found: list[dict[str, int | float]]) -> Image.Image:
    cell_w = 220
    cell_h = 170
    cols = 5
    rows = max(1, (len(found) + cols - 1) // cols)
    sheet = Image.new("RGB", (cols * cell_w, rows * cell_h), "white")
    draw = ImageDraw.Draw(sheet)

    for idx, item in enumerate(found):
        col = idx % cols
        row = idx // cols
        x = int(item["x"])
        y = int(item["y"])
        w = int(item["width"])
        h = int(item["height"])
        pad = 18
        crop = source.crop((max(0, x - pad), max(0, y - pad), min(source.width, x + w + pad), min(source.height, y + h + pad)))
        crop.thumbnail((cell_w - 20, cell_h - 38), Image.Resampling.LANCZOS)
        ox = col * cell_w + 10
        oy = row * cell_h + 28
        sheet.paste(crop, (ox, oy))
        draw.text((col * cell_w + 10, row * cell_h + 8), f"#{item['id']}  x={x} y={y}", fill=(0, 0, 0))

    return sheet


def main() -> None:
    parser = argparse.ArgumentParser(description="Extract highlighted parking-map regions from a raster plan.")
    parser.add_argument("source", nargs="?", default=str(DEFAULT_SOURCE))
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT))
    parser.add_argument("--prefix", default=None)
    args = parser.parse_args()

    source_path = Path(args.source)
    output = Path(args.output)
    prefix = args.prefix or source_path.stem.lower().replace(" ", "-")

    output.mkdir(parents=True, exist_ok=True)
    source = Image.open(source_path).convert("RGB")

    enhanced = enhance(source)
    enhanced.save(output / f"{prefix}-enhanced.png", optimize=True)

    raw_mask = green_mask(source, close=False)
    mask = green_mask(source, close=True)
    mask.save(output / f"{prefix}-green-mask.png", optimize=True)

    found = components(mask, raw_mask)
    (output / f"{prefix}-green-spots.json").write_text(json.dumps(found, indent=2), encoding="utf-8")
    with (output / f"{prefix}-green-spots.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["id", "label", "x", "y", "width", "height", "center_x", "center_y", "area", "aspect", "confidence"])
        writer.writeheader()
        writer.writerows(found)

    draw_overlay(source, found).save(output / f"{prefix}-green-spots-overlay.png", optimize=True)
    contact_sheet(source, found).save(output / f"{prefix}-green-crops.png", optimize=True)

    print(f"file={source_path}")
    print(f"source={source.width}x{source.height}")
    print(f"enhanced={enhanced.width}x{enhanced.height}")
    print(f"green_components={len(found)}")
    print(output)


if __name__ == "__main__":
    main()
