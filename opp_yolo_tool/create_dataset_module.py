#!/usr/bin/env python
from __future__ import annotations

import argparse
import json
import math
import random
import re
import shutil
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from utils import get_logger

LOG = get_logger(__name__)

try:
    import arcpy
except Exception as ex:
    arcpy = None
    LOG.error("arcpy import failed: %s", ex)


@dataclass(slots=True)
class TileInfo:
    name: str
    image_path: Path
    geom: object
    xmin: float
    ymin: float
    xmax: float
    ymax: float


@dataclass(slots=True)
class AnnotationShape:
    class_id: int
    kind: str
    points: list[tuple[float, float]]


def _find_tile_grid(tiles_folder: Path) -> Path | None:
    candidates = [
        tiles_folder / "shapes" / "Tile_Grid_Pixels.shp",
        tiles_folder.parent / "shapes" / "Tile_Grid_Pixels.shp",
    ]
    for c in candidates:
        if c.exists():
            return c
    return None


def _load_tiles(tiles_folder: Path) -> list[Path]:
    images_dir = tiles_folder / "Images"
    if not images_dir.exists():
        return []
    exts = {".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"}
    return sorted([p for p in images_dir.iterdir() if p.is_file() and p.suffix.lower() in exts])


def _load_grid(grid_fc: Path) -> dict[str, tuple[object, float, float, float, float]]:
    result: dict[str, tuple[object, float, float, float, float]] = {}
    fields = ["TileName", "SHAPE@"]
    with arcpy.da.SearchCursor(str(grid_fc), fields) as cur:
        for tile_name, geom in cur:
            if not tile_name or geom is None:
                continue
            ext = geom.extent
            result[str(tile_name)] = (geom, float(ext.XMin), float(ext.YMin), float(ext.XMax), float(ext.YMax))
    return result


def _split_trailing_number(value: str) -> tuple[str, int] | None:
    m = re.match(r"^(.*?)(\d+)$", value)
    if not m:
        return None
    return m.group(1), int(m.group(2))


def _build_grid_aliases(grid: dict[str, tuple[object, float, float, float, float]]) -> dict[str, tuple[object, float, float, float, float]]:
    aliases: dict[str, tuple[object, float, float, float, float]] = {}
    for tile_name, row in grid.items():
        key = tile_name.strip().lower()
        if not key:
            continue
        aliases[key] = row

        split = _split_trailing_number(key)
        if split is None:
            continue
        prefix, number = split

        compact_prefix = prefix[:-1] if prefix.endswith("_") else prefix
        aliases[f"{compact_prefix}{number}"] = row

        if number > 0:
            aliases[f"{prefix}{number - 1}"] = row
            aliases[f"{compact_prefix}{number - 1}"] = row

    return aliases


def _normalize_aprx_path(aprx_path: str | None) -> str | None:
    if not aprx_path:
        return None
    value = aprx_path.strip()
    if value.lower().startswith("file:///"):
        value = value[8:]
    return value.replace("/", "\\")


def _find_layer_by_name(name: str, aprx_path: str | None):
    target_project = None
    try:
        target_project = arcpy.mp.ArcGISProject("CURRENT")
    except Exception:
        target_project = None

    if target_project is None:
        normalized_aprx = _normalize_aprx_path(aprx_path)
        if normalized_aprx:
            target_project = arcpy.mp.ArcGISProject(normalized_aprx)

    if target_project is None:
        return None

    maps = []
    try:
        if target_project.activeMap is not None:
            maps.append(target_project.activeMap)
    except Exception:
        pass

    try:
        maps.extend(target_project.listMaps())
    except Exception:
        pass

    seen = set()
    for mp in maps:
        if mp is None:
            continue
        for layer in mp.listLayers():
            if layer is None or not getattr(layer, "isFeatureLayer", False):
                continue
            layer_name = str(getattr(layer, "name", "")).strip().lower()
            if layer_name != name.strip().lower() or layer_name in seen:
                continue

            seen.add(layer_name)
            try:
                return layer.dataSource
            except Exception:
                try:
                    return layer
                except Exception:
                    continue
    return None


def _clamp(v: float, lo: float, hi: float) -> float:
    if v < lo:
        return lo
    if v > hi:
        return hi
    return v


def _to_yolo_bbox(tile: TileInfo, extent) -> tuple[float, float, float, float] | None:
    txmin, tymin, txmax, tymax = tile.xmin, tile.ymin, tile.xmax, tile.ymax
    iw = txmax - txmin
    ih = tymax - tymin
    if iw <= 0 or ih <= 0:
        return None

    x1 = _clamp(float(extent.XMin), txmin, txmax)
    y1 = _clamp(float(extent.YMin), tymin, tymax)
    x2 = _clamp(float(extent.XMax), txmin, txmax)
    y2 = _clamp(float(extent.YMax), tymin, tymax)
    if x2 <= x1 or y2 <= y1:
        return None

    cx = ((x1 + x2) * 0.5 - txmin) / iw
    cy = (tymax - (y1 + y2) * 0.5) / ih
    w = (x2 - x1) / iw
    h = (y2 - y1) / ih

    if not (math.isfinite(cx) and math.isfinite(cy) and math.isfinite(w) and math.isfinite(h)):
        return None

    cx = _clamp(cx, 0.0, 1.0)
    cy = _clamp(cy, 0.0, 1.0)
    w = _clamp(w, 0.0, 1.0)
    h = _clamp(h, 0.0, 1.0)
    if w <= 0.0 or h <= 0.0:
        return None
    return cx, cy, w, h


def _to_norm_xy(tile: TileInfo, x: float, y: float) -> tuple[float, float]:
    iw = tile.xmax - tile.xmin
    ih = tile.ymax - tile.ymin
    if iw <= 0 or ih <= 0:
        return 0.0, 0.0
    nx = _clamp((x - tile.xmin) / iw, 0.0, 1.0)
    ny = _clamp((tile.ymax - y) / ih, 0.0, 1.0)
    return float(nx), float(ny)


def _polygon_area(points: list[tuple[float, float]]) -> float:
    if len(points) < 3:
        return 0.0
    area = 0.0
    for i in range(len(points)):
        x1, y1 = points[i]
        x2, y2 = points[(i + 1) % len(points)]
        area += x1 * y2 - x2 * y1
    return abs(area) * 0.5


def _largest_polygon_from_geom(tile: TileInfo, geom) -> list[tuple[float, float]]:
    best: list[tuple[float, float]] = []
    best_area = 0.0
    try:
        for part in geom:
            ring: list[tuple[float, float]] = []
            for pt in part:
                if pt is None:
                    if len(ring) >= 3:
                        area = _polygon_area(ring)
                        if area > best_area:
                            best = ring
                            best_area = area
                    ring = []
                    continue
                ring.append(_to_norm_xy(tile, float(pt.X), float(pt.Y)))

            if len(ring) >= 3:
                area = _polygon_area(ring)
                if area > best_area:
                    best = ring
                    best_area = area
    except Exception:
        return []

    return best


def _obb_from_polygon(points: list[tuple[float, float]]) -> list[tuple[float, float]]:
    if len(points) < 3:
        return []
    pts = np.array(points, dtype=np.float32)
    rect = cv2.minAreaRect(pts)
    box = cv2.boxPoints(rect)
    out: list[tuple[float, float]] = []
    for px, py in box:
        if not (math.isfinite(float(px)) and math.isfinite(float(py))):
            continue
        out.append((float(_clamp(float(px), 0.0, 1.0)), float(_clamp(float(py), 0.0, 1.0))))
    return out if len(out) == 4 else []


def _build_tile_infos(tiles_folder: Path, images: list[Path]) -> list[TileInfo]:
    grid_fc = _find_tile_grid(tiles_folder)
    if grid_fc is None:
        raise FileNotFoundError("Tile_Grid_Pixels.shp not found in tiles/shapes")

    grid = _load_grid(grid_fc)
    aliases = _build_grid_aliases(grid)
    infos: list[TileInfo] = []
    for img in images:
        tile_name = img.stem
        row = aliases.get(tile_name.strip().lower())
        if row is None:
            continue
        geom, xmin, ymin, xmax, ymax = row
        infos.append(TileInfo(name=tile_name, image_path=img, geom=geom, xmin=xmin, ymin=ymin, xmax=xmax, ymax=ymax))

    if not infos:
        sample_images = [p.stem for p in images[:5]]
        sample_grid = list(grid.keys())[:5]
        LOG.error("Tile-name match failed. image stems sample=%s grid TileName sample=%s", sample_images, sample_grid)

    return infos


def _write_labels(
    tile: TileInfo,
    labels_path: Path,
    layers: list[tuple[int, object]],
    dataset_type: str,
) -> tuple[int, list[AnnotationShape]]:
    lines: list[str] = []
    shapes: list[AnnotationShape] = []
    total = 0
    mode = (dataset_type or "Detection").strip().lower()
    for class_id, layer in layers:
        try:
            with arcpy.da.SearchCursor(layer, ["SHAPE@"], spatial_filter=tile.geom, spatial_relationship="INTERSECTS") as cur:
                for (geom,) in cur:
                    if geom is None:
                        continue
                    inter = geom.intersect(tile.geom, 4)
                    if inter is None:
                        continue
                    ext = inter.extent
                    if ext is None:
                        continue
                    if mode == "detection":
                        bbox = _to_yolo_bbox(tile, ext)
                        if bbox is None:
                            continue
                        cx, cy, w, h = bbox
                        lines.append(f"{class_id} {cx:.6f} {cy:.6f} {w:.6f} {h:.6f}")
                        x1 = cx - w * 0.5
                        y1 = cy - h * 0.5
                        x2 = cx + w * 0.5
                        y2 = cy + h * 0.5
                        shapes.append(AnnotationShape(class_id=class_id, kind="bbox", points=[(x1, y1), (x2, y1), (x2, y2), (x1, y2)]))
                        total += 1
                        continue

                    poly = _largest_polygon_from_geom(tile, inter)
                    if len(poly) < 3:
                        continue

                    if mode == "segmentation":
                        coords = " ".join(f"{x:.6f} {y:.6f}" for x, y in poly)
                        lines.append(f"{class_id} {coords}")
                        shapes.append(AnnotationShape(class_id=class_id, kind="poly", points=poly))
                        total += 1
                        continue

                    obb = _obb_from_polygon(poly)
                    if len(obb) != 4:
                        continue
                    coords = " ".join(f"{x:.6f} {y:.6f}" for x, y in obb)
                    lines.append(f"{class_id} {coords}")
                    shapes.append(AnnotationShape(class_id=class_id, kind="obb", points=obb))
                    total += 1
        except Exception as ex:
            LOG.warning("Layer read failed for '%s': %s", getattr(layer, "name", "layer"), ex)

    labels_path.write_text("\n".join(lines), encoding="utf-8")
    return total, shapes


def _draw_debug_annotations(
    image_path: Path,
    debug_image_path: Path,
    shapes: list[AnnotationShape],
    class_names: dict[int, str],
) -> None:
    image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
    if image is None:
        LOG.warning("Failed to read image for debug draw: %s", image_path)
        return

    h, w = image.shape[:2]
    if h <= 0 or w <= 0:
        LOG.warning("Invalid image size for debug draw: %s", image_path)
        return

    for shape in shapes:
        pts = shape.points
        if not pts:
            continue
        pix = []
        for px, py in pts:
            if not (math.isfinite(px) and math.isfinite(py)):
                continue
            x = int(max(0, min(w - 1, round(px * w))))
            y = int(max(0, min(h - 1, round(py * h))))
            pix.append((x, y))
        if len(pix) < 2:
            continue

        if shape.kind == "bbox" and len(pix) >= 4:
            xs = [p[0] for p in pix]
            ys = [p[1] for p in pix]
            x1, x2 = min(xs), max(xs)
            y1, y2 = min(ys), max(ys)
            if x2 <= x1 or y2 <= y1:
                continue
            cv2.rectangle(image, (x1, y1), (x2, y2), (0, 220, 0), 1)
            anchor_x, anchor_y = x1, y1
        else:
            poly = np.array(pix, dtype=np.int32).reshape((-1, 1, 2))
            cv2.polylines(image, [poly], isClosed=True, color=(0, 220, 0), thickness=1)
            anchor_x, anchor_y = pix[0]

        class_name = class_names.get(shape.class_id, f"class_{shape.class_id}")
        text = f"{shape.class_id}:{class_name}"
        text_y = anchor_y - 4 if anchor_y > 10 else min(h - 2, anchor_y + 10)
        cv2.putText(image, text, (anchor_x + 1, text_y), cv2.FONT_HERSHEY_SIMPLEX, 0.35, (0, 220, 0), 1, cv2.LINE_AA)

    debug_image_path.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(debug_image_path), image)


def _is_almost_black_or_white(image_path: Path, dominance_threshold: float = 0.985) -> bool:
    image = cv2.imread(str(image_path), cv2.IMREAD_GRAYSCALE)
    if image is None or image.size == 0:
        return False

    total = float(image.size)
    black_ratio = float((image <= 10).sum()) / total
    white_ratio = float((image >= 245).sum()) / total
    # Фильтруем как «пустые» не только почти полностью черные/белые,
    # но и комбинации вида «черный + белый» (например, белая полоса сверху и черный остальной тайл).
    return (black_ratio + white_ratio) >= dominance_threshold


def _split(items: list[TileInfo], train: int, val: int, test: int, seed: int) -> dict[str, list[TileInfo]]:
    rnd = random.Random(seed)
    data = items.copy()
    rnd.shuffle(data)
    n = len(data)
    n_train = int(round(n * train / 100.0))
    n_val = int(round(n * val / 100.0))
    if n_train + n_val > n:
        n_val = max(0, n - n_train)
    n_test = max(0, n - n_train - n_val)
    return {
        "train": data[:n_train],
        "valid": data[n_train:n_train + n_val],
        "test": data[n_train + n_val:n_train + n_val + n_test],
    }


def _copy_and_label(
    split_name: str,
    items: list[TileInfo],
    dataset_root: Path,
    layers: list[tuple[int, object]],
    debug_enabled: bool,
    debug_dir: Path,
    class_names: dict[int, str],
    dataset_type: str,
) -> tuple[int, int]:
    images_dir = dataset_root / split_name / "images"
    labels_dir = dataset_root / split_name / "labels"
    images_dir.mkdir(parents=True, exist_ok=True)
    labels_dir.mkdir(parents=True, exist_ok=True)

    copied = 0
    labels = 0
    skipped_empty_visual = 0
    for tile in items:
        dst_lbl = labels_dir / f"{tile.image_path.stem}.txt"
        written, shapes = _write_labels(tile, dst_lbl, layers, dataset_type)

        if written == 0 and _is_almost_black_or_white(tile.image_path):
            skipped_empty_visual += 1
            try:
                dst_lbl.unlink(missing_ok=True)
            except Exception:
                pass
            LOG.info("Skipped near-empty visual tile without annotations: %s", tile.image_path.name)
            continue

        dst_img = images_dir / tile.image_path.name
        shutil.copy2(tile.image_path, dst_img)
        copied += 1
        labels += written

        if debug_enabled:
            debug_image = debug_dir / split_name / tile.image_path.name
            _draw_debug_annotations(dst_img, debug_image, shapes, class_names)

    if skipped_empty_visual > 0:
        LOG.info("Split '%s': skipped near-empty visual tiles without annotations: %s", split_name, skipped_empty_visual)

    return copied, labels


def main(argv: list[str] | None = None) -> int:
    if arcpy is None:
        LOG.error("arcpy is required")
        return 2

    parser = argparse.ArgumentParser()
    parser.add_argument("--tiles-folder", required=True)
    parser.add_argument("--dataset-root", required=True)
    parser.add_argument("--train", type=int, required=True)
    parser.add_argument("--val", type=int, required=True)
    parser.add_argument("--test", type=int, required=True)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--layers", required=True)
    parser.add_argument("--dataset-type", required=False, default="Detection")
    parser.add_argument("--aprx", required=False)
    parser.add_argument("--debug", action="store_true")
    parser.add_argument("--debug-dir", required=False)
    args = parser.parse_args(argv)

    tiles_folder = Path(args.tiles_folder)
    dataset_root = Path(args.dataset_root)

    if not tiles_folder.exists():
        LOG.error("Tiles folder not found: %s", tiles_folder)
        return 3

    selected_layers = [x.strip() for x in args.layers.split("|") if x.strip()]
    if not selected_layers:
        LOG.error("No layers provided")
        return 4

    class_names = {idx: name for idx, name in enumerate(selected_layers)}
    dataset_type = (args.dataset_type or "Detection").strip()

    debug_enabled = bool(args.debug)
    debug_dir = Path(args.debug_dir) if args.debug_dir else (dataset_root / "debug")
    if debug_enabled:
        debug_dir.mkdir(parents=True, exist_ok=True)

    layer_refs: list[tuple[int, object]] = []
    for idx, layer_name in enumerate(selected_layers):
        layer = _find_layer_by_name(layer_name, args.aprx)
        if layer is None:
            LOG.warning("Layer not found in map: %s", layer_name)
            continue
        layer_refs.append((idx, layer))

    if not layer_refs:
        LOG.error("None of selected layers found in active map")
        return 5

    images = _load_tiles(tiles_folder)
    tile_infos = _build_tile_infos(tiles_folder, images)
    if not tile_infos:
        LOG.error("No tile images matched Tile_Grid_Pixels")
        return 6

    split = _split(tile_infos, args.train, args.val, args.test, args.seed)

    total_images = 0
    total_labels = 0
    split_stats: dict[str, dict[str, int]] = {}
    for split_name in ("train", "valid", "test"):
        copied, labels = _copy_and_label(
            split_name,
            split.get(split_name, []),
            dataset_root,
            layer_refs,
            debug_enabled,
            debug_dir,
            class_names,
            dataset_type,
        )
        split_stats[split_name] = {"images": copied, "labels": labels}
        total_images += copied
        total_labels += labels

    summary = {
        "tiles_folder": str(tiles_folder),
        "dataset_root": str(dataset_root),
        "layers": selected_layers,
        "dataset_type": dataset_type,
        "split": split_stats,
        "total_images": total_images,
        "total_labels": total_labels,
    }
    (dataset_root / "dataset_build_summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    LOG.info("Dataset build completed. images=%s labels=%s", total_images, total_labels)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
