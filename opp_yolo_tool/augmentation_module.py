#!/usr/bin/env python
from __future__ import annotations

import argparse
import json
import math
import os
import random
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import numpy as np

from utils import get_logger

LOG = get_logger(__name__)

try:
    import cv2
except Exception as ex:
    cv2 = None
    LOG.error("cv2 import failed: %s", ex)

try:
    import yaml
except Exception:
    yaml = None


@dataclass(slots=True)
class YoloObject:
    cls_id: int
    points: list[tuple[float, float]]
    source_kind: str


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset-root", required=False)
    parser.add_argument("--config", required=False)
    parser.add_argument("--seed", type=int, required=False)
    parser.add_argument("--apply-to-val", action="store_true")
    parser.add_argument("--apply-to-test", action="store_true")
    parser.add_argument("--max-per-image", type=int, default=2)
    parser.add_argument("--debug", action="store_true")
    parser.add_argument("--debug-dir", required=False)
    parser.add_argument("--preview-image", required=False)
    parser.add_argument("--preview-output", required=False)
    parser.add_argument("--preview-summary", required=False)
    parser.add_argument("--preview-op", required=False)
    parser.add_argument("--post-background-limit", type=int, default=-1)
    parser.add_argument("--post-background-limit-is-percent", action="store_true")
    parser.add_argument("--post-class-balance", action="store_true")
    parser.add_argument("--post-balance-method", required=False, default="median")
    return parser.parse_args(argv)


def _clamp(v: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, v))


def _to_bool(v, default: bool = False) -> bool:
    if isinstance(v, bool):
        return v
    if isinstance(v, str):
        return v.strip().lower() in {"1", "true", "yes", "on"}
    if isinstance(v, (int, float)):
        return bool(v)
    return default


def _to_float(v, default: float = 0.0) -> float:
    try:
        return float(v)
    except Exception:
        return float(default)


def _load_yaml(path: Path) -> dict:
    if not path.exists():
        raise FileNotFoundError(f"Config not found: {path}")

    text = path.read_text(encoding="utf-8")
    if yaml is not None:
        data = yaml.safe_load(text)
        return data if isinstance(data, dict) else {}

    data: dict = {}
    section = None
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.endswith(":"):
            key = line[:-1].strip()
            if key in {"geometry", "color", "noise", "advanced"}:
                section = key
                data.setdefault(section, {})
            else:
                section = None
                data[key] = {}
            continue
        if ":" not in line:
            continue

        key, value = [x.strip() for x in line.split(":", 1)]
        if section and raw.startswith("  "):
            if value.startswith("{") and value.endswith("}"):
                item = {}
                payload = value[1:-1]
                for part in payload.split(","):
                    if ":" not in part:
                        continue
                    k, v = [x.strip() for x in part.split(":", 1)]
                    item[k] = v
                data[section][key] = {
                    "value": _to_float(item.get("value", 0.0), 0.0),
                    "prob": _to_float(item.get("prob", 0.0), 0.0),
                    "enabled": _to_bool(item.get("enabled", False), False),
                }
            else:
                data[section][key] = _to_bool(value, False)
        else:
            data[key] = value
    return data


def _read_label_file(path: Path) -> list[YoloObject]:
    if not path.exists():
        return []
    out: list[YoloObject] = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line:
            continue
        parts = line.split()
        if len(parts) < 5:
            continue
        try:
            cls_id = int(float(parts[0]))
            values = [float(x) for x in parts[1:]]
        except Exception:
            continue

        if len(values) == 4:
            cx, cy, w, h = values
            x1 = cx - w * 0.5
            y1 = cy - h * 0.5
            x2 = cx + w * 0.5
            y2 = cy + h * 0.5
            pts = [(x1, y1), (x2, y1), (x2, y2), (x1, y2)]
            out.append(YoloObject(cls_id=cls_id, points=pts, source_kind="bbox"))
        elif len(values) >= 6 and len(values) % 2 == 0:
            pts = []
            for i in range(0, len(values), 2):
                pts.append((values[i], values[i + 1]))
            out.append(YoloObject(cls_id=cls_id, points=pts, source_kind="poly"))
    return out


def _bbox_from_points(points: list[tuple[float, float]]) -> tuple[float, float, float, float]:
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


def _apply_random_crop(image: np.ndarray, objs: list[YoloObject], config: dict, rng: random.Random) -> tuple[np.ndarray, list[YoloObject]]:
    geo = config.get("geometry", {})
    apply, value, _ = _apply_scalar_prob(rng, geo.get("random_crop", {}))
    if not apply:
        return image, objs

    crop_scale = float(_clamp(value, 0.5, 1.0))
    if crop_scale >= 0.999:
        return image, objs

    h, w = image.shape[:2]
    cw = max(2, int(round(w * crop_scale)))
    ch = max(2, int(round(h * crop_scale)))
    if cw >= w or ch >= h:
        return image, objs

    x0 = rng.randint(0, w - cw)
    y0 = rng.randint(0, h - ch)
    x1 = x0 + cw
    y1 = y0 + ch

    cropped = image[y0:y1, x0:x1]
    resized = cv2.resize(cropped, (w, h), interpolation=cv2.INTER_LINEAR)

    cx0 = x0 / float(w)
    cy0 = y0 / float(h)
    cx1 = x1 / float(w)
    cy1 = y1 / float(h)
    sx = 1.0 / max(1e-8, (cx1 - cx0))
    sy = 1.0 / max(1e-8, (cy1 - cy0))

    transformed: list[YoloObject] = []
    for obj in objs:
        new_pts: list[tuple[float, float]] = []
        for x, y in obj.points:
            nx = (x - cx0) * sx
            ny = (y - cy0) * sy
            new_pts.append((float(_clamp(nx, 0.0, 1.0)), float(_clamp(ny, 0.0, 1.0))))

        bx1, by1, bx2, by2 = _bbox_from_points(new_pts)
        if (bx2 - bx1) < 1e-4 or (by2 - by1) < 1e-4:
            continue

        transformed.append(YoloObject(cls_id=obj.cls_id, points=new_pts, source_kind=obj.source_kind))

    return resized, transformed


def _write_label_file(path: Path, objs: Iterable[YoloObject]) -> None:
    lines: list[str] = []
    for obj in objs:
        pts = [(_clamp(x, 0.0, 1.0), _clamp(y, 0.0, 1.0)) for x, y in obj.points]
        if len(pts) < 3:
            continue
        if obj.source_kind == "bbox":
            xs = [p[0] for p in pts]
            ys = [p[1] for p in pts]
            x1, x2 = min(xs), max(xs)
            y1, y2 = min(ys), max(ys)
            w = max(0.0, x2 - x1)
            h = max(0.0, y2 - y1)
            if w < 1e-5 or h < 1e-5:
                continue
            cx = x1 + w * 0.5
            cy = y1 + h * 0.5
            lines.append(f"{obj.cls_id} {cx:.6f} {cy:.6f} {w:.6f} {h:.6f}")
        else:
            flat = []
            for x, y in pts:
                flat.append(f"{x:.6f}")
                flat.append(f"{y:.6f}")
            lines.append(f"{obj.cls_id} {' '.join(flat)}")

    path.write_text("\n".join(lines), encoding="utf-8")


def _apply_points_affine(points: list[tuple[float, float]], m: np.ndarray) -> list[tuple[float, float]]:
    if not points:
        return []
    pts = np.array(points, dtype=np.float32)
    ones = np.ones((pts.shape[0], 1), dtype=np.float32)
    hpts = np.hstack([pts, ones])
    t = (m @ hpts.T).T
    z = np.clip(t[:, 2], 1e-8, None)
    x = t[:, 0] / z
    y = t[:, 1] / z
    return [(float(_clamp(px, 0.0, 1.0)), float(_clamp(py, 0.0, 1.0))) for px, py in zip(x, y)]


def _identity_h() -> np.ndarray:
    return np.eye(3, dtype=np.float32)


def _hflip_h() -> np.ndarray:
    return np.array([[-1, 0, 1], [0, 1, 0], [0, 0, 1]], dtype=np.float32)


def _vflip_h() -> np.ndarray:
    return np.array([[1, 0, 0], [0, -1, 1], [0, 0, 1]], dtype=np.float32)


def _rot90_h() -> np.ndarray:
    return np.array([[0, -1, 1], [1, 0, 0], [0, 0, 1]], dtype=np.float32)


def _rot180_h() -> np.ndarray:
    return np.array([[-1, 0, 1], [0, -1, 1], [0, 0, 1]], dtype=np.float32)


def _rot270_h() -> np.ndarray:
    return np.array([[0, 1, 0], [-1, 0, 1], [0, 0, 1]], dtype=np.float32)


def _warp(img: np.ndarray, h: np.ndarray) -> np.ndarray:
    hpx = np.array([[img.shape[1], 0, 0], [0, img.shape[0], 0], [0, 0, 1]], dtype=np.float32)
    hpx_inv = np.linalg.inv(hpx)
    m = hpx @ h @ hpx_inv
    return cv2.warpPerspective(img, m, (img.shape[1], img.shape[0]), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT101)


def _apply_hsv(img: np.ndarray, hue: float, sat: float, val: float) -> np.ndarray:
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV).astype(np.float32)
    hsv[:, :, 0] = np.mod(hsv[:, :, 0] + hue * 180.0, 180.0)
    hsv[:, :, 1] = np.clip(hsv[:, :, 1] * sat, 0.0, 255.0)
    hsv[:, :, 2] = np.clip(hsv[:, :, 2] * val, 0.0, 255.0)
    return cv2.cvtColor(hsv.astype(np.uint8), cv2.COLOR_HSV2BGR)


def _apply_scalar_prob(rng: random.Random, spec: dict | bool) -> tuple[bool, float, float]:
    if isinstance(spec, bool):
        return spec, 1.0, 1.0
    enabled = _to_bool(spec.get("enabled", False), False)
    base_value = _to_float(spec.get("value", 0.0), 0.0)
    value_min = _to_float(spec.get("value_min", base_value), base_value)
    value_max = _to_float(spec.get("value_max", base_value), base_value)
    if value_min > value_max:
        value_min, value_max = value_max, value_min
    value = rng.uniform(value_min, value_max)
    prob = _to_float(spec.get("prob", 0.0), 0.0)
    if not enabled:
        return False, value, prob
    return rng.random() <= prob, value, prob


def _build_random_h(config: dict, rng: random.Random) -> np.ndarray:
    geo = config.get("geometry", {})
    h = _identity_h()

    apply, value, _ = _apply_scalar_prob(rng, geo.get("shear_x", {}))
    if apply and abs(value) > 1e-8:
        sh = np.array([[1, np.tan(np.radians(value)), 0], [0, 1, 0], [0, 0, 1]], dtype=np.float32)
        h = sh @ h

    apply, value, _ = _apply_scalar_prob(rng, geo.get("shear_y", {}))
    if apply and abs(value) > 1e-8:
        sh = np.array([[1, 0, 0], [np.tan(np.radians(value)), 1, 0], [0, 0, 1]], dtype=np.float32)
        h = sh @ h

    apply, value, _ = _apply_scalar_prob(rng, geo.get("scale", {}))
    if apply and abs(value - 1.0) > 1e-8:
        sc = np.array([[value, 0, (1.0 - value) * 0.5], [0, value, (1.0 - value) * 0.5], [0, 0, 1]], dtype=np.float32)
        h = sc @ h

    apply, value, _ = _apply_scalar_prob(rng, geo.get("translate_x", {}))
    if apply and abs(value) > 1e-8:
        tr = np.array([[1, 0, value], [0, 1, 0], [0, 0, 1]], dtype=np.float32)
        h = tr @ h

    apply, value, _ = _apply_scalar_prob(rng, geo.get("translate_y", {}))
    if apply and abs(value) > 1e-8:
        tr = np.array([[1, 0, 0], [0, 1, value], [0, 0, 1]], dtype=np.float32)
        h = tr @ h

    apply, value, _ = _apply_scalar_prob(rng, geo.get("perspective", {}))
    if apply and abs(value) > 1e-10:
        p1 = rng.uniform(-value, value)
        p2 = rng.uniform(-value, value)
        ph = np.array([[1, 0, 0], [0, 1, 0], [p1, p2, 1]], dtype=np.float32)
        h = ph @ h

    return h


def _color_noise_ops(img: np.ndarray, config: dict, rng: random.Random) -> np.ndarray:
    out = img
    color = config.get("color", {})
    noise = config.get("noise", {})

    apply_h, hue, _ = _apply_scalar_prob(rng, color.get("hue", {}))
    apply_s, sat, _ = _apply_scalar_prob(rng, color.get("saturation", {}))
    apply_v, val, _ = _apply_scalar_prob(rng, color.get("value_brightness", {}))
    if apply_h or apply_s or apply_v:
        out = _apply_hsv(out, hue if apply_h else 0.0, sat if apply_s else 1.0, val if apply_v else 1.0)

    apply, value, _ = _apply_scalar_prob(rng, color.get("contrast", {}))
    if apply:
        out = cv2.convertScaleAbs(out, alpha=max(0.0, value), beta=0)

    apply, value, _ = _apply_scalar_prob(rng, color.get("clahe", {}))
    if apply:
        lab = cv2.cvtColor(out, cv2.COLOR_BGR2LAB)
        l, a, b = cv2.split(lab)
        clahe = cv2.createCLAHE(clipLimit=max(1.0, value), tileGridSize=(8, 8))
        out = cv2.cvtColor(cv2.merge([clahe.apply(l), a, b]), cv2.COLOR_LAB2BGR)

    apply, _, _ = _apply_scalar_prob(rng, color.get("grayscale", {}))
    if apply:
        g = cv2.cvtColor(out, cv2.COLOR_BGR2GRAY)
        out = cv2.cvtColor(g, cv2.COLOR_GRAY2BGR)

    apply, value, _ = _apply_scalar_prob(rng, color.get("solarize", {}))
    if apply:
        threshold = int(_clamp(value, 0, 255))
        out = np.where(out >= threshold, 255 - out, out).astype(np.uint8)

    apply, value, _ = _apply_scalar_prob(rng, color.get("posterize", {}))
    if apply:
        bits = int(_clamp(round(value), 1, 8))
        shift = 8 - bits
        out = ((out >> shift) << shift).astype(np.uint8)

    apply, _, _ = _apply_scalar_prob(rng, color.get("equalize", {}))
    if apply:
        ycrcb = cv2.cvtColor(out, cv2.COLOR_BGR2YCrCb)
        ycrcb[:, :, 0] = cv2.equalizeHist(ycrcb[:, :, 0])
        out = cv2.cvtColor(ycrcb, cv2.COLOR_YCrCb2BGR)

    apply, value, _ = _apply_scalar_prob(rng, color.get("auto_contrast", {}))
    if apply:
        cutoff = int(_clamp(value, 0, 20))
        if cutoff > 0:
            low = np.percentile(out, cutoff)
            high = np.percentile(out, 100 - cutoff)
        else:
            low = float(np.min(out))
            high = float(np.max(out))

        if high > low:
            out = np.clip((out.astype(np.float32) - low) * (255.0 / (high - low)), 0, 255).astype(np.uint8)

    apply, value, _ = _apply_scalar_prob(rng, noise.get("gaussian_blur", {}))
    if apply:
        k = int(max(1, round(value)))
        if k % 2 == 0:
            k += 1
        out = cv2.GaussianBlur(out, (k, k), 0)

    apply, value, _ = _apply_scalar_prob(rng, noise.get("median_blur", {}))
    if apply:
        k = int(max(1, round(value)))
        if k % 2 == 0:
            k += 1
        out = cv2.medianBlur(out, k)

    apply, value, _ = _apply_scalar_prob(rng, noise.get("gaussian_noise", {}))
    if apply and value > 0:
        ga = np.random.normal(0, value, out.shape).astype(np.float32)
        out = np.clip(out.astype(np.float32) + ga, 0, 255).astype(np.uint8)

    apply, value, _ = _apply_scalar_prob(rng, noise.get("salt_and_pepper", {}))
    if apply and value > 0:
        p = _clamp(value, 0.0, 0.25)
        rnd = np.random.rand(out.shape[0], out.shape[1])
        out[rnd < (p * 0.5)] = 0
        out[rnd > (1.0 - p * 0.5)] = 255

    apply, _, _ = _apply_scalar_prob(rng, noise.get("random_shadow", {}))
    if apply:
        h, w = out.shape[:2]
        x1, y1 = rng.randint(0, w - 1), rng.randint(0, h - 1)
        x2, y2 = rng.randint(0, w - 1), rng.randint(0, h - 1)
        mask = np.zeros((h, w), dtype=np.uint8)
        cv2.line(mask, (x1, y1), (x2, y2), color=255, thickness=max(20, w // 8))
        shadow = out.copy()
        shadow[mask > 0] = (shadow[mask > 0] * 0.5).astype(np.uint8)
        out = shadow

    apply, value, _ = _apply_scalar_prob(rng, noise.get("rain_fog", {}))
    if apply:
        alpha = _clamp(value, 0.0, 1.0)
        fog = np.full_like(out, 220, dtype=np.uint8)
        out = cv2.addWeighted(out, 1.0 - alpha * 0.35, fog, alpha * 0.35, 0)

    return out


def _transform_objects(objs: list[YoloObject], h: np.ndarray) -> list[YoloObject]:
    out: list[YoloObject] = []
    for obj in objs:
        pts = _apply_points_affine(obj.points, h)
        out.append(YoloObject(cls_id=obj.cls_id, points=pts, source_kind=obj.source_kind))
    return out


def _deterministic_variants(config: dict) -> list[tuple[str, np.ndarray]]:
    geo = config.get("geometry", {})
    variants: list[tuple[str, np.ndarray]] = []
    use_rot90 = _to_bool(geo.get("rotate_90_cw", False), False)
    use_rot180 = _to_bool(geo.get("rotate_180", False), False)
    use_rot270 = _to_bool(geo.get("rotate_270_cw", False), False)
    use_fliph = _to_bool(geo.get("flip_horizontal", False), False)
    use_flipv = _to_bool(geo.get("flip_vertical", False), False)

    if use_rot90:
        variants.append(("rot90", _rot90_h()))
    if use_rot180:
        variants.append(("rot180", _rot180_h()))
    if use_rot270:
        variants.append(("rot270", _rot270_h()))
    if use_fliph:
        variants.append(("fliph", _hflip_h()))
    if use_flipv:
        variants.append(("flipv", _vflip_h()))

    # Дополнительные детерминированные комбинации:
    # FlipH + Rotate90 и FlipH + Rotate270
    if use_fliph and use_rot90:
        variants.append(("fliph_rot90", _rot90_h() @ _hflip_h()))
    if use_fliph and use_rot270:
        variants.append(("fliph_rot270", _rot270_h() @ _hflip_h()))

    return variants


def _apply_cutout_erasing(img: np.ndarray, config: dict, rng: random.Random) -> np.ndarray:
    adv = config.get("advanced", {})
    out = img

    apply, value, _ = _apply_scalar_prob(rng, adv.get("cutout", {}))
    if apply and value > 0:
        h, w = out.shape[:2]
        n = max(1, int(round(value * 10)))
        for _ in range(n):
            rw = rng.randint(max(4, w // 20), max(8, w // 4))
            rh = rng.randint(max(4, h // 20), max(8, h // 4))
            x = rng.randint(0, max(0, w - rw))
            y = rng.randint(0, max(0, h - rh))
            out[y:y + rh, x:x + rw] = rng.randint(0, 255)

    apply, _, _ = _apply_scalar_prob(rng, adv.get("erasing", {}))
    if apply:
        h, w = out.shape[:2]
        rw = rng.randint(max(10, w // 10), max(20, w // 3))
        rh = rng.randint(max(10, h // 10), max(20, h // 3))
        x = rng.randint(0, max(0, w - rw))
        y = rng.randint(0, max(0, h - rh))
        out[y:y + rh, x:x + rw] = 0

    return out


def _collect_split_items(split_dir: Path) -> list[tuple[Path, Path]]:
    images_dir = split_dir / "images"
    labels_dir = split_dir / "labels"
    if not images_dir.exists() or not labels_dir.exists():
        return []

    exts = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}
    items: list[tuple[Path, Path]] = []
    for image_path in sorted(images_dir.iterdir()):
        if image_path.suffix.lower() not in exts:
            continue
        label_path = labels_dir / f"{image_path.stem}.txt"
        if label_path.exists():
            items.append((image_path, label_path))
    return items


def _save_variant(split_dir: Path, stem: str, tag: str, img: np.ndarray, objs: list[YoloObject]) -> None:
    img_path = split_dir / "images" / f"{stem}__{tag}.jpg"
    lbl_path = split_dir / "labels" / f"{stem}__{tag}.txt"
    cv2.imwrite(str(img_path), img)
    _write_label_file(lbl_path, objs)


def _draw_debug_annotations(img: np.ndarray, objs: list[YoloObject]) -> np.ndarray:
    out = img.copy()
    h, w = out.shape[:2]
    if h <= 0 or w <= 0:
        return out

    for obj in objs:
        pts = obj.points
        if not pts:
            continue

        xs = [p[0] for p in pts if math.isfinite(p[0])]
        ys = [p[1] for p in pts if math.isfinite(p[1])]
        if not xs or not ys:
            continue

        x1n, x2n = min(xs), max(xs)
        y1n, y2n = min(ys), max(ys)
        if not (math.isfinite(x1n) and math.isfinite(x2n) and math.isfinite(y1n) and math.isfinite(y2n)):
            continue

        pix = []
        for px, py in pts:
            if not (math.isfinite(px) and math.isfinite(py)):
                continue
            ix = int(max(0, min(w - 1, round(px * (w - 1)))))
            iy = int(max(0, min(h - 1, round(py * (h - 1)))))
            pix.append((ix, iy))

        if obj.source_kind == "bbox":
            x1 = int(max(0, min(w - 1, round(x1n * (w - 1)))))
            y1 = int(max(0, min(h - 1, round(y1n * (h - 1)))))
            x2 = int(max(0, min(w - 1, round(x2n * (w - 1)))))
            y2 = int(max(0, min(h - 1, round(y2n * (h - 1)))))
            if x2 <= x1 or y2 <= y1:
                continue
            cv2.rectangle(out, (x1, y1), (x2, y2), (0, 220, 0), 1)
            anchor_x, anchor_y = x1, y1
        else:
            if len(pix) < 3:
                continue
            poly = np.array(pix, dtype=np.int32).reshape((-1, 1, 2))
            cv2.polylines(out, [poly], isClosed=True, color=(0, 220, 0), thickness=1)
            anchor_x, anchor_y = pix[0]

        label = f"class: {obj.cls_id}"
        text_y = anchor_y - 4 if anchor_y > 10 else min(h - 2, anchor_y + 10)
        cv2.putText(out, label, (anchor_x + 1, text_y), cv2.FONT_HERSHEY_SIMPLEX, 0.35, (0, 220, 0), 1, cv2.LINE_AA)

    return out


def _save_debug_variant(debug_dir: Path, split_name: str, stem: str, tag: str, img: np.ndarray, objs: list[YoloObject]) -> None:
    debug_split_dir = debug_dir / split_name
    debug_split_dir.mkdir(parents=True, exist_ok=True)
    debug_img = _draw_debug_annotations(img, objs)
    debug_img_path = debug_split_dir / f"{stem}__{tag}.jpg"
    cv2.imwrite(str(debug_img_path), debug_img)


def _load_item(image_path: Path, label_path: Path) -> tuple[np.ndarray | None, list[YoloObject]]:
    image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
    if image is None:
        return None, []
    return image, _read_label_file(label_path)


def _remap_objects(objs: list[YoloObject], x_off: float, y_off: float, scale_x: float, scale_y: float) -> list[YoloObject]:
    out: list[YoloObject] = []
    for obj in objs:
        pts = [
            (
                float(_clamp(x_off + (x * scale_x), 0.0, 1.0)),
                float(_clamp(y_off + (y * scale_y), 0.0, 1.0)),
            )
            for x, y in obj.points
        ]
        out.append(YoloObject(cls_id=obj.cls_id, points=pts, source_kind=obj.source_kind))
    return out


def _apply_mosaic(image: np.ndarray, objs: list[YoloObject], items: list[tuple[Path, Path]], config: dict, rng: random.Random) -> tuple[np.ndarray, list[YoloObject]]:
    adv = config.get("advanced", {})
    apply, _, _ = _apply_scalar_prob(rng, adv.get("mosaic_4_img", {}))
    if not apply or not items:
        return image, objs

    h, w = image.shape[:2]
    pool = items.copy()
    rng.shuffle(pool)
    selected = pool[:3]

    sources: list[tuple[np.ndarray, list[YoloObject]]] = [(image, objs)]
    for img_p, lbl_p in selected:
        donor_img, donor_objs = _load_item(img_p, lbl_p)
        if donor_img is not None:
            sources.append((donor_img, donor_objs))

    while len(sources) < 4:
        sources.append((image, objs))

    canvas = np.zeros_like(image)
    out_objs: list[YoloObject] = []
    cells = [(0, 0), (0.5, 0), (0, 0.5), (0.5, 0.5)]
    for idx, (src_img, src_objs) in enumerate(sources[:4]):
        resized = cv2.resize(src_img, (w // 2, h // 2), interpolation=cv2.INTER_LINEAR)
        x0 = 0 if idx % 2 == 0 else (w // 2)
        y0 = 0 if idx < 2 else (h // 2)
        canvas[y0:y0 + (h // 2), x0:x0 + (w // 2)] = resized

        off_x, off_y = cells[idx]
        out_objs.extend(_remap_objects(src_objs, off_x, off_y, 0.5, 0.5))

    return canvas, out_objs


def _apply_mixup(image: np.ndarray, objs: list[YoloObject], items: list[tuple[Path, Path]], config: dict, rng: random.Random) -> tuple[np.ndarray, list[YoloObject]]:
    adv = config.get("advanced", {})
    apply, value, _ = _apply_scalar_prob(rng, adv.get("mixup", {}))
    if not apply or not items:
        return image, objs

    donor_path = rng.choice(items)
    donor_img, donor_objs = _load_item(donor_path[0], donor_path[1])
    if donor_img is None:
        return image, objs

    donor_img = cv2.resize(donor_img, (image.shape[1], image.shape[0]), interpolation=cv2.INTER_LINEAR)
    alpha = float(_clamp(value, 0.0, 1.0))
    if alpha <= 0.0:
        alpha = 0.5
    mixed = cv2.addWeighted(image, 1.0 - alpha, donor_img, alpha, 0.0)
    return mixed, list(objs) + list(donor_objs)


def _build_polygon_mask(points_px: list[tuple[int, int]], w: int, h: int) -> np.ndarray:
    mask = np.zeros((h, w), dtype=np.uint8)
    if len(points_px) < 3:
        return mask
    poly = np.array(points_px, dtype=np.int32).reshape((-1, 1, 2))
    cv2.fillPoly(mask, [poly], 255)
    return mask


def _apply_copy_paste(image: np.ndarray, objs: list[YoloObject], items: list[tuple[Path, Path]], config: dict, rng: random.Random) -> tuple[np.ndarray, list[YoloObject]]:
    adv = config.get("advanced", {})
    apply, value, _ = _apply_scalar_prob(rng, adv.get("copy_paste", {}))
    if not apply or not items:
        return image, objs

    donor_path = rng.choice(items)
    donor_img, donor_objs = _load_item(donor_path[0], donor_path[1])
    if donor_img is None or not donor_objs:
        return image, objs

    h, w = image.shape[:2]
    donor_img = cv2.resize(donor_img, (w, h), interpolation=cv2.INTER_LINEAR)
    target = image.copy()
    out_objs = list(objs)

    count = max(1, int(round(_clamp(value, 1, 10))))
    candidates = donor_objs.copy()
    rng.shuffle(candidates)

    for obj in candidates[:count]:
        pts_px = [(int(round(x * (w - 1))), int(round(y * (h - 1)))) for x, y in obj.points]
        if not pts_px:
            continue

        xs = [p[0] for p in pts_px]
        ys = [p[1] for p in pts_px]
        x1, y1 = max(0, min(xs)), max(0, min(ys))
        x2, y2 = min(w - 1, max(xs)), min(h - 1, max(ys))
        bw, bh = x2 - x1 + 1, y2 - y1 + 1
        if bw < 2 or bh < 2:
            continue

        patch = donor_img[y1:y2 + 1, x1:x2 + 1].copy()
        if obj.source_kind == "poly":
            local_pts = [(p[0] - x1, p[1] - y1) for p in pts_px]
            mask = _build_polygon_mask(local_pts, bw, bh)
        else:
            mask = np.full((bh, bw), 255, dtype=np.uint8)

        tx = rng.randint(0, max(0, w - bw))
        ty = rng.randint(0, max(0, h - bh))

        roi = target[ty:ty + bh, tx:tx + bw]
        inv = cv2.bitwise_not(mask)
        bg = cv2.bitwise_and(roi, roi, mask=inv)
        fg = cv2.bitwise_and(patch, patch, mask=mask)
        target[ty:ty + bh, tx:tx + bw] = cv2.add(bg, fg)

        new_pts: list[tuple[float, float]] = []
        for px, py in pts_px:
            nx = (tx + (px - x1)) / float(max(1, w - 1))
            ny = (ty + (py - y1)) / float(max(1, h - 1))
            new_pts.append((float(_clamp(nx, 0.0, 1.0)), float(_clamp(ny, 0.0, 1.0))))

        out_objs.append(YoloObject(cls_id=obj.cls_id, points=new_pts, source_kind=obj.source_kind))

    return target, out_objs


def _process_split(
    split_dir: Path,
    config: dict,
    rng: random.Random,
    max_per_image: int,
    debug_enabled: bool,
    debug_dir: Path,
) -> dict:
    stats = {"split": split_dir.name, "images": 0, "created": 0}
    items = _collect_split_items(split_dir)
    if not items:
        return stats

    deterministic = _deterministic_variants(config)
    has_random_pipeline = _has_random_pipeline_enabled(config)
    for image_path, label_path in items:
        image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
        if image is None:
            continue

        objects = _read_label_file(label_path)
        stem = image_path.stem
        stats["images"] += 1

        for tag, h in deterministic:
            aug_img = _warp(image, h)
            aug_objs = _transform_objects(objects, h)
            _save_variant(split_dir, stem, tag, aug_img, aug_objs)
            if debug_enabled:
                _save_debug_variant(debug_dir, split_dir.name, stem, tag, aug_img, aug_objs)
            stats["created"] += 1

        if has_random_pipeline:
            for idx in range(max(0, max_per_image)):
                h_rand = _build_random_h(config, rng)
                aug_img = _warp(image, h_rand)
                aug_objs = _transform_objects(objects, h_rand)
                aug_img, aug_objs = _apply_random_crop(aug_img, aug_objs, config, rng)
                aug_img, aug_objs = _apply_mosaic(aug_img, aug_objs, items, config, rng)
                aug_img, aug_objs = _apply_mixup(aug_img, aug_objs, items, config, rng)
                aug_img, aug_objs = _apply_copy_paste(aug_img, aug_objs, items, config, rng)
                aug_img = _color_noise_ops(aug_img, config, rng)
                aug_img = _apply_cutout_erasing(aug_img, config, rng)
                _save_variant(split_dir, stem, f"rand{idx + 1}", aug_img, aug_objs)
                if debug_enabled:
                    _save_debug_variant(debug_dir, split_dir.name, stem, f"rand{idx + 1}", aug_img, aug_objs)
                stats["created"] += 1

    return stats


def _safe_unlink(path: Path) -> bool:
    try:
        if path.exists():
            path.unlink()
            return True
    except Exception as ex:
        LOG.warning("Failed to delete file %s: %s", path, ex)
    return False


def _extract_label_classes(label_path: Path) -> tuple[bool, list[int]]:
    if not label_path.exists():
        return True, []

    classes: list[int] = []
    for raw in label_path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line:
            continue
        parts = line.split()
        if not parts:
            continue
        try:
            cls_id = int(float(parts[0]))
            classes.append(cls_id)
        except Exception:
            continue
    return len(classes) == 0, classes


def _enumerate_split_samples(split_dir: Path) -> list[dict]:
    images_dir = split_dir / "images"
    labels_dir = split_dir / "labels"
    if not images_dir.exists() or not labels_dir.exists():
        return []

    exts = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}
    samples: list[dict] = []
    for image_path in sorted(images_dir.iterdir()):
        if image_path.suffix.lower() not in exts:
            continue

        label_path = labels_dir / f"{image_path.stem}.txt"
        is_background, classes = _extract_label_classes(label_path)
        samples.append(
            {
                "image": image_path,
                "label": label_path,
                "is_background": is_background,
                "classes": classes,
            }
        )
    return samples


def _limit_background_samples(
    split_dir: Path,
    rng: random.Random,
    limit: int,
    is_percent: bool,
) -> dict:
    samples = _enumerate_split_samples(split_dir)
    if not samples:
        return {
            "split": split_dir.name,
            "applied": False,
            "reason": "split_not_found_or_empty",
            "before_background": 0,
            "after_background": 0,
            "removed": 0,
            "allowed": 0,
        }

    background = [s for s in samples if s["is_background"]]
    objects_count = len(samples) - len(background)
    before_bg = len(background)

    if before_bg <= 0 or limit < 0:
        return {
            "split": split_dir.name,
            "applied": False,
            "reason": "disabled_or_no_background",
            "before_background": before_bg,
            "after_background": before_bg,
            "removed": 0,
            "allowed": before_bg,
        }

    if is_percent:
        pct = _clamp(limit / 100.0, 0.0, 1.0)
        if pct <= 0.0:
            allowed = 0
        elif pct >= 1.0:
            allowed = before_bg
        else:
            # Ограничение доли background в финальном сплите:
            # background / (objects + background) <= pct
            # => background <= objects * pct / (1 - pct)
            allowed = int(math.floor(objects_count * pct / (1.0 - pct)))
    else:
        allowed = max(0, limit)
    allowed = max(0, allowed)

    if before_bg <= allowed:
        return {
            "split": split_dir.name,
            "applied": True,
            "reason": "within_limit",
            "before_background": before_bg,
            "after_background": before_bg,
            "removed": 0,
            "allowed": allowed,
            "objects": objects_count,
        }

    rng.shuffle(background)
    to_remove = background[allowed:]
    removed = 0
    for sample in to_remove:
        deleted_img = _safe_unlink(sample["image"])
        deleted_lbl = _safe_unlink(sample["label"])
        if deleted_img or deleted_lbl:
            removed += 1

    after_bg = max(0, before_bg - removed)
    LOG.info(
        "Post-balance background limit split=%s before=%s allowed=%s removed=%s after=%s",
        split_dir.name,
        before_bg,
        allowed,
        removed,
        after_bg,
    )

    return {
        "split": split_dir.name,
        "applied": True,
        "reason": "trimmed" if removed > 0 else "within_limit",
        "before_background": before_bg,
        "after_background": after_bg,
        "removed": removed,
        "allowed": allowed,
        "objects": objects_count,
    }


def _resolve_balance_target(counts: list[int], method: str) -> int:
    if not counts:
        return 0
    m = (method or "median").strip().lower()
    if m == "minimum":
        return int(min(counts))
    if m == "average":
        return int(round(sum(counts) / float(len(counts))))
    ordered = sorted(counts)
    n = len(ordered)
    mid = n // 2
    if n % 2 == 1:
        return int(ordered[mid])
    return int(round((ordered[mid - 1] + ordered[mid]) / 2.0))


def _build_class_index(samples: list[dict]) -> dict[int, list[dict]]:
    per_class: dict[int, list[dict]] = {}
    for sample in samples:
        if sample["is_background"]:
            continue
        uniq = sorted(set(int(c) for c in sample["classes"]))
        for cls_id in uniq:
            per_class.setdefault(cls_id, []).append(sample)
    return per_class


def _downsample_train_by_class(split_dir: Path, rng: random.Random, method: str) -> dict:
    samples = _enumerate_split_samples(split_dir)
    if not samples:
        return {
            "split": split_dir.name,
            "applied": False,
            "reason": "split_not_found_or_empty",
            "method": (method or "median").strip().lower(),
            "target": 0,
            "before": {},
            "after": {},
            "removed": 0,
        }

    class_index = _build_class_index(samples)
    before = {int(k): len(v) for k, v in class_index.items()}
    if len(before) <= 1:
        return {
            "split": split_dir.name,
            "applied": False,
            "reason": "single_or_no_class",
            "method": (method or "median").strip().lower(),
            "target": next(iter(before.values()), 0),
            "before": before,
            "after": before,
            "removed": 0,
        }

    target = _resolve_balance_target(list(before.values()), method)
    target = max(1, int(target))

    remove_candidates: set[str] = set()
    for cls_id, cls_samples in class_index.items():
        if len(cls_samples) <= target:
            continue
        shuffled = list(cls_samples)
        rng.shuffle(shuffled)
        for sample in shuffled[target:]:
            key = str(sample["image"])
            remove_candidates.add(key)

    removed = 0
    for sample in samples:
        key = str(sample["image"])
        if key not in remove_candidates:
            continue
        deleted_img = _safe_unlink(sample["image"])
        deleted_lbl = _safe_unlink(sample["label"])
        if deleted_img or deleted_lbl:
            removed += 1

    after_samples = _enumerate_split_samples(split_dir)
    after_index = _build_class_index(after_samples)
    after = {int(k): len(v) for k, v in after_index.items()}

    LOG.info(
        "Post-balance class downsample split=%s method=%s target=%s removed=%s before=%s after=%s",
        split_dir.name,
        (method or "median").strip().lower(),
        target,
        removed,
        before,
        after,
    )

    return {
        "split": split_dir.name,
        "applied": True,
        "reason": "balanced",
        "method": (method or "median").strip().lower(),
        "target": target,
        "before": before,
        "after": after,
        "removed": removed,
    }

def _is_enabled_spec(spec) -> bool:
    if isinstance(spec, bool):
        return bool(spec)
    if isinstance(spec, dict):
        return _to_bool(spec.get("enabled", False), False) and _to_float(spec.get("prob", 0.0), 0.0) > 0.0
    return False


def _has_random_pipeline_enabled(config: dict) -> bool:
    geometry = config.get("geometry", {})
    color = config.get("color", {})
    noise = config.get("noise", {})
    advanced = config.get("advanced", {})

    random_geometry_keys = ["shear_x", "shear_y", "scale", "translate_x", "translate_y", "perspective", "random_crop"]
    random_color_keys = ["hue", "saturation", "value_brightness", "contrast", "clahe", "auto_contrast", "grayscale", "solarize", "posterize", "equalize"]
    random_noise_keys = ["gaussian_blur", "median_blur", "gaussian_noise", "salt_and_pepper", "random_shadow", "rain_fog"]
    random_advanced_keys = ["mosaic_4_img", "mixup", "copy_paste", "cutout", "erasing"]

    for key in random_geometry_keys:
        if _is_enabled_spec(geometry.get(key, {})):
            return True
    for key in random_color_keys:
        if _is_enabled_spec(color.get(key, {})):
            return True
    for key in random_noise_keys:
        if _is_enabled_spec(noise.get(key, {})):
            return True
    for key in random_advanced_keys:
        if _is_enabled_spec(advanced.get(key, {})):
            return True

    return False


def run(
    dataset_root: Path,
    config_path: Path,
    seed_override: int | None,
    force_apply_val: bool,
    force_apply_test: bool,
    max_per_image: int,
    debug_enabled: bool,
    debug_dir: Path,
    post_background_limit: int,
    post_background_limit_is_percent: bool,
    post_class_balance: bool,
    post_balance_method: str,
) -> dict:
    if cv2 is None:
        raise RuntimeError("cv2 is required for augmentation_module.py")

    cfg = _load_yaml(config_path)
    seed = seed_override if seed_override is not None else int(_to_float(cfg.get("seed", 0), 0))
    if seed <= 0:
        seed = random.randint(1, 10_000_000)

    random.seed(seed)
    np.random.seed(seed)
    rng = random.Random(seed)

    apply_to_val = force_apply_val or _to_bool(cfg.get("apply_to_val", False), False)
    apply_to_test = force_apply_test or _to_bool(cfg.get("apply_to_test", False), False)

    splits = [dataset_root / "train"]
    if apply_to_val:
        splits.append(dataset_root / "valid")
    if apply_to_test:
        splits.append(dataset_root / "test")

    result = {
        "seed": seed,
        "config": str(config_path),
        "dataset_root": str(dataset_root),
        "post_balance": {
            "background_limit": int(post_background_limit),
            "background_limit_is_percent": bool(post_background_limit_is_percent),
            "class_balance": bool(post_class_balance),
            "balance_method": (post_balance_method or "median").strip().lower(),
        },
        "splits": [],
        "total_created": 0,
        "post_background": [],
        "post_class_balance": {},
    }

    if debug_enabled:
        debug_dir.mkdir(parents=True, exist_ok=True)

    for split_dir in splits:
        stat = _process_split(
            split_dir,
            cfg,
            rng,
            max_per_image=max_per_image,
            debug_enabled=debug_enabled,
            debug_dir=debug_dir,
        )
        result["splits"].append(stat)
        result["total_created"] += int(stat.get("created", 0))

    for split_dir in splits:
        bg_stat = _limit_background_samples(
            split_dir=split_dir,
            rng=rng,
            limit=int(post_background_limit),
            is_percent=bool(post_background_limit_is_percent),
        )
        result["post_background"].append(bg_stat)

    if post_class_balance:
        train_split = dataset_root / "train"
        result["post_class_balance"] = _downsample_train_by_class(
            split_dir=train_split,
            rng=rng,
            method=post_balance_method,
        )

    return result


def _force_enabled_prob_one(cfg: dict) -> dict:
    out = json.loads(json.dumps(cfg))
    for group_name in ["geometry", "color", "noise", "advanced"]:
        group = out.get(group_name, {})
        if not isinstance(group, dict):
            continue
        for key, spec in list(group.items()):
            if isinstance(spec, dict) and _to_bool(spec.get("enabled", False), False):
                spec["prob"] = 1.0
    return out


def run_preview(
    preview_image: Path,
    preview_output: Path,
    config_path: Path,
    seed_override: int | None,
    preview_op: str | None,
) -> dict:
    if cv2 is None:
        raise RuntimeError("cv2 is required for augmentation_module.py")
    if not preview_image.exists():
        raise FileNotFoundError(f"Preview image not found: {preview_image}")

    cfg = _load_yaml(config_path)
    cfg = _force_enabled_prob_one(cfg)

    seed = seed_override if seed_override is not None else int(_to_float(cfg.get("seed", 0), 0))
    if seed <= 0:
        seed = random.randint(1, 10_000_000)
    rng = random.Random(seed)
    np.random.seed(seed)

    img = cv2.imread(str(preview_image), cv2.IMREAD_COLOR)
    if img is None:
        raise RuntimeError(f"Failed to read image: {preview_image}")

    objs: list[YoloObject] = []
    applied_steps: list[str] = []

    deterministic_geo = [
        ("rotate_90_cw", _rot90_h),
        ("rotate_180", _rot180_h),
        ("rotate_270_cw", _rot270_h),
        ("flip_horizontal", _hflip_h),
        ("flip_vertical", _vflip_h),
    ]
    random_geo = ["shear_x", "shear_y", "scale", "translate_x", "translate_y", "perspective", "random_crop"]
    color_ops = ["hue", "saturation", "value_brightness", "contrast", "clahe", "auto_contrast", "grayscale", "solarize", "posterize", "equalize"]
    noise_ops = ["gaussian_blur", "median_blur", "gaussian_noise", "salt_and_pepper", "random_shadow", "rain_fog"]
    advanced_preview_supported = ["cutout", "erasing"]

    candidates: list[tuple[str, str]] = []
    geo = cfg.get("geometry", {})
    color = cfg.get("color", {})
    noise = cfg.get("noise", {})
    advanced = cfg.get("advanced", {})

    for key, _ in deterministic_geo:
        if _to_bool(geo.get(key, False), False):
            candidates.append(("det_geo", key))

    for key in random_geo:
        spec = geo.get(key, {})
        if isinstance(spec, dict) and _to_bool(spec.get("enabled", False), False):
            candidates.append(("rand_geo", key))

    for key in color_ops:
        spec = color.get(key, {})
        if isinstance(spec, dict) and _to_bool(spec.get("enabled", False), False):
            candidates.append(("color", key))

    for key in noise_ops:
        spec = noise.get(key, {})
        if isinstance(spec, dict) and _to_bool(spec.get("enabled", False), False):
            candidates.append(("noise", key))

    for key in advanced_preview_supported:
        spec = advanced.get(key, {})
        if isinstance(spec, dict) and _to_bool(spec.get("enabled", False), False):
            candidates.append(("advanced", key))

    if not candidates:
        preview_output.parent.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(preview_output), img)
        return {
            "seed": seed,
            "preview_image": str(preview_image),
            "preview_output": str(preview_output),
            "applied_steps": ["no_enabled_preview_transform"],
        }

    selected = None
    if preview_op:
        normalized = preview_op.strip().lower()
        for c in candidates:
            if c[1] == normalized:
                selected = c
                break
    group, op = selected if selected is not None else rng.choice(candidates)

    if group == "det_geo":
        h = None
        for key, mat_fn in deterministic_geo:
            if key == op:
                h = mat_fn()
                break
        if h is not None:
            img = _warp(img, h)
            objs = _transform_objects(objs, h)
            applied_steps.append(op)
    elif group == "rand_geo":
        if op == "random_crop":
            single_cfg = {"geometry": {"random_crop": dict(geo.get("random_crop", {}))}}
            spec = single_cfg["geometry"]["random_crop"]
            spec["enabled"] = True
            spec["prob"] = 1.0
            img, objs = _apply_random_crop(img, objs, single_cfg, rng)
            applied_steps.append(op)
        else:
            single_cfg = {"geometry": {op: dict(geo.get(op, {}))}}
            spec = single_cfg["geometry"][op]
            spec["enabled"] = True
            spec["prob"] = 1.0
            h_rand = _build_random_h(single_cfg, rng)
            if not np.allclose(h_rand, _identity_h()):
                img = _warp(img, h_rand)
                objs = _transform_objects(objs, h_rand)
            applied_steps.append(op)
    elif group == "color":
        single_cfg = {"color": {op: dict(color.get(op, {}))}, "noise": {}}
        spec = single_cfg["color"][op]
        spec["enabled"] = True
        spec["prob"] = 1.0
        img = _color_noise_ops(img, single_cfg, rng)
        applied_steps.append(op)
    elif group == "noise":
        single_cfg = {"color": {}, "noise": {op: dict(noise.get(op, {}))}}
        spec = single_cfg["noise"][op]
        spec["enabled"] = True
        spec["prob"] = 1.0
        img = _color_noise_ops(img, single_cfg, rng)
        applied_steps.append(op)
    elif group == "advanced":
        single_cfg = {"advanced": {op: dict(advanced.get(op, {}))}}
        spec = single_cfg["advanced"][op]
        spec["enabled"] = True
        spec["prob"] = 1.0
        img = _apply_cutout_erasing(img, single_cfg, rng)
        applied_steps.append(op)

    preview_output.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(preview_output), img)

    return {
        "seed": seed,
        "preview_image": str(preview_image),
        "preview_output": str(preview_output),
        "applied_steps": applied_steps,
    }


def main(argv: list[str] | None = None) -> int:
    argv = argv if argv is not None else os.sys.argv[1:]
    args = parse_args(argv)

    if args.preview_image and args.preview_output:
        config_path = Path(args.config) if args.config else Path("augmentation_config.yaml")
        summary = run_preview(
            preview_image=Path(args.preview_image),
            preview_output=Path(args.preview_output),
            config_path=config_path,
            seed_override=args.seed,
            preview_op=args.preview_op,
        )
        if args.preview_summary:
            Path(args.preview_summary).write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        LOG.info("Preview completed. Output=%s", summary.get("preview_output"))
        return 0

    if not args.dataset_root:
        LOG.error("--dataset-root is required for dataset augmentation mode")
        return 2

    dataset_root = Path(args.dataset_root)
    config_path = Path(args.config) if args.config else dataset_root / "augmentation_config.yaml"
    debug_enabled = bool(args.debug)
    debug_dir = Path(args.debug_dir) if args.debug_dir else (dataset_root / "debug")

    if not dataset_root.exists():
        LOG.error("Dataset root not found: %s", dataset_root)
        return 2

    try:
        summary = run(
            dataset_root=dataset_root,
            config_path=config_path,
            seed_override=args.seed,
            force_apply_val=bool(args.apply_to_val),
            force_apply_test=bool(args.apply_to_test),
            max_per_image=max(0, int(args.max_per_image)),
            debug_enabled=debug_enabled,
            debug_dir=debug_dir,
            post_background_limit=int(args.post_background_limit),
            post_background_limit_is_percent=bool(args.post_background_limit_is_percent),
            post_class_balance=bool(args.post_class_balance),
            post_balance_method=(args.post_balance_method or "median"),
        )

        summary_path = dataset_root / "augmentation_run_summary.json"
        summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        LOG.info("Augmentation completed. Created=%s, summary=%s", summary.get("total_created", 0), summary_path)
        return 0
    except Exception as ex:
        LOG.exception("Augmentation failed: %s", ex)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())