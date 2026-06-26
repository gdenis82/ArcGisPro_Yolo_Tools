#!/usr/bin/env python
"""
predict_module.py

SAHI-only предсказание для ортофото с записью результатов в shapefile через arcpy.
Пайплайн:
1) SAHI slicing + NMS на исходном орто
2) Сохранение единого JSON с предсказаниями
3) Формирование Detected_Points.shp / Detected_BBoxes.shp / Detected_Masks.shp

Merged-слои не создаются.
"""

import argparse
import datetime as dt
import json
import os
import sys
from pathlib import Path

from utils import (
    get_logger,
    is_debug_enabled,
    to_float_scalar,
    get_raster_info,
    get_saved_global_setting,
    set_saved_global_settings,
)

LOG = get_logger(__name__)

try:
    import arcpy
except Exception:
    arcpy = None

DEBUG = is_debug_enabled("OPP_YOLO_DEBUG_PREDICT_MODULE")


def _pixel_to_map(extent, px, py, cell_w, cell_h):
    x_map = extent.XMin + (px * cell_w)
    y_map = extent.YMax - (py * cell_h)
    return x_map, y_map


def _bbox_pixels_to_coords(extent, x1, y1, x2, y2, cell_w, cell_h):
    x1_map, y1_map = _pixel_to_map(extent, x1, y1, cell_w, cell_h)
    x2_map, y2_map = _pixel_to_map(extent, x2, y2, cell_w, cell_h)
    return [(x1_map, y1_map), (x1_map, y2_map), (x2_map, y2_map), (x2_map, y1_map), (x1_map, y1_map)]


def _pixel_polygon_to_geo(extent, poly_px, cell_w, cell_h):
    return [(extent.XMin + (pt[0] * cell_w), extent.YMax - (pt[1] * cell_h)) for pt in poly_px]


def create_empty_shapefile(path, shape_type="POINT", spatial_ref=None):
    folder = os.path.dirname(path)
    name = os.path.basename(path)
    if not arcpy:
        return

    arcpy.env.overwriteOutput = True
    sr = spatial_ref if spatial_ref is not None else arcpy.SpatialReference(4326)
    arcpy.CreateFeatureclass_management(folder, name, shape_type, spatial_reference=sr)
    try:
        arcpy.AddField_management(path, "confidence", "DOUBLE")
        arcpy.AddField_management(path, "class", "TEXT")
        arcpy.AddField_management(path, "source_img", "TEXT")
        arcpy.AddField_management(path, "tile_id", "TEXT")
        arcpy.AddField_management(path, "pixel_w", "DOUBLE")
        arcpy.AddField_management(path, "pixel_h", "DOUBLE")
        arcpy.AddField_management(path, "xmin", "DOUBLE")
        arcpy.AddField_management(path, "xmax", "DOUBLE")
        arcpy.AddField_management(path, "ymin", "DOUBLE")
        arcpy.AddField_management(path, "ymax", "DOUBLE")
    except Exception:
        pass


def save_point(shapefile, x, y, conf, cls, src_img=None, tile_id=None, pixel_w=None, pixel_h=None, bounds=None):
    if not arcpy:
        return
    fields = [
        "SHAPE@XY",
        "confidence",
        "class",
        "source_img",
        "tile_id",
        "pixel_w",
        "pixel_h",
        "xmin",
        "xmax",
        "ymin",
        "ymax",
    ]
    xmin, xmax, ymin, ymax = bounds if bounds is not None else (None, None, None, None)
    with arcpy.da.InsertCursor(shapefile, fields) as cursor:
        cursor.insertRow([
            (x, y),
            float(conf),
            str(cls),
            src_img or "",
            tile_id or "",
            float(pixel_w or 0.0),
            float(pixel_h or 0.0),
            xmin,
            xmax,
            ymin,
            ymax,
        ])


def save_polygon(shapefile, coords, conf, cls, src_img=None, tile_id=None, pixel_w=None, pixel_h=None, bounds=None):
    if not arcpy:
        return
    sr = arcpy.Describe(shapefile).spatialReference
    array = arcpy.Array([arcpy.Point(x, y) for (x, y) in coords])
    poly = arcpy.Polygon(array, sr)
    fields = [
        "SHAPE@",
        "confidence",
        "class",
        "source_img",
        "tile_id",
        "pixel_w",
        "pixel_h",
        "xmin",
        "xmax",
        "ymin",
        "ymax",
    ]
    xmin, xmax, ymin, ymax = bounds if bounds is not None else (None, None, None, None)
    with arcpy.da.InsertCursor(shapefile, fields) as cursor:
        cursor.insertRow([
            poly,
            float(conf),
            str(cls),
            src_img or "",
            tile_id or "",
            float(pixel_w or 0.0),
            float(pixel_h or 0.0),
            xmin,
            xmax,
            ymin,
            ymax,
        ])


def save_polygon_geometry(shapefile, poly, conf, cls, src_img=None, tile_id=None, pixel_w=None, pixel_h=None, bounds=None):
    if not arcpy or poly is None:
        return
    fields = [
        "SHAPE@",
        "confidence",
        "class",
        "source_img",
        "tile_id",
        "pixel_w",
        "pixel_h",
        "xmin",
        "xmax",
        "ymin",
        "ymax",
    ]
    xmin, xmax, ymin, ymax = bounds if bounds is not None else (None, None, None, None)
    with arcpy.da.InsertCursor(shapefile, fields) as cursor:
        cursor.insertRow([
            poly,
            float(conf),
            str(cls),
            src_img or "",
            tile_id or "",
            float(pixel_w or 0.0),
            float(pixel_h or 0.0),
            xmin,
            xmax,
            ymin,
            ymax,
        ])


def _extract_model_name(model_path):
    try:
        return Path(model_path).stem
    except Exception:
        return "model"


def _format_conf_tag(confidence):
    try:
        conf_num = int(round(float(confidence) * 10))
        return f"conf{conf_num:02d}"
    except Exception:
        return "conf00"


def _build_experiment_name(tile_size, confidence, model_path):
    ts = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    model_name = _extract_model_name(model_path)
    conf_tag = _format_conf_tag(confidence)
    return f"{ts}_{int(tile_size)}px_{conf_tag}_{model_name}"


def _resolve_detection_root(tiles_dir):
    tiles_path = Path(tiles_dir)
    parent = tiles_path.parent
    if tiles_path.name.lower().endswith("px") and parent.name.lower() == "tiles":
        return str(parent.parent / "Detection_Results")
    if tiles_path.name.lower() == "tiles":
        return str(parent / "Detection_Results")
    return str(parent / "Detection_Results")


def _resolve_ortho_from_tiles_dir(tiles_dir):
    try:
        tiles_path = Path(tiles_dir)
        if tiles_path.name.lower().endswith("px") and tiles_path.parent.name.lower() == "tiles":
            eomw_dir = tiles_path.parent.parent
        elif tiles_path.name.lower() == "tiles":
            eomw_dir = tiles_path.parent
        else:
            eomw_dir = tiles_path.parent

        orthos_dir = eomw_dir / "Products" / "Orthos"
        if not orthos_dir.exists():
            return None

        candidates = []
        for pattern in ("*.tif", "*.tiff", "*.img", "*.vrt"):
            candidates.extend(sorted(orthos_dir.glob(pattern)))
        return str(candidates[0]) if candidates else None
    except Exception:
        return None


def _poly_area_px(poly_px):
    if not poly_px or len(poly_px) < 3:
        return 0.0
    try:
        pts = [(float(p[0]), float(p[1])) for p in poly_px]
        if pts[0] != pts[-1]:
            pts.append(pts[0])
        s = 0.0
        for i in range(len(pts) - 1):
            x1, y1 = pts[i]
            x2, y2 = pts[i + 1]
            s += (x1 * y2) - (x2 * y1)
        return abs(s) * 0.5
    except Exception:
        return 0.0


def _select_primary_mask_polygon(mask_polys):
    best_poly = None
    best_area = 0.0
    if not isinstance(mask_polys, list):
        return None
    for poly in mask_polys:
        if not isinstance(poly, list) or len(poly) < 3:
            continue
        area = _poly_area_px(poly)
        if area > best_area:
            best_area = area
            best_poly = poly
    return best_poly


def _mask_poly_to_geometry(poly_px, extent, cell_w, cell_h, sr):
    if not isinstance(poly_px, list) or len(poly_px) < 3:
        return None
    coords = _pixel_polygon_to_geo(extent, poly_px, cell_w, cell_h)
    if len(coords) < 4:
        return None
    if coords[0] != coords[-1]:
        coords.append(coords[0])
    arr = arcpy.Array([arcpy.Point(x, y) for x, y in coords])
    poly = arcpy.Polygon(arr, sr)
    if poly is None:
        return None

    is_empty_attr = getattr(poly, "isEmpty", None)
    if callable(is_empty_attr):
        try:
            if bool(is_empty_attr()):
                return None
        except Exception:
            pass
    elif isinstance(is_empty_attr, bool):
        if is_empty_attr:
            return None

    try:
        if int(getattr(poly, "pointCount", 0)) <= 0:
            return None
    except Exception:
        pass

    return poly


def _build_union_mask_geometry(mask_polys, extent, cell_w, cell_h, sr):
    merged = None
    for poly_px in mask_polys:
        geom = _mask_poly_to_geometry(poly_px, extent, cell_w, cell_h, sr)
        if geom is None:
            continue
        if merged is None:
            merged = geom
        else:
            try:
                merged = merged.union(geom)
            except Exception:
                pass
    if merged is None:
        return None

    is_empty_attr = getattr(merged, "isEmpty", None)
    if callable(is_empty_attr):
        try:
            if bool(is_empty_attr()):
                return None
        except Exception:
            pass
    elif isinstance(is_empty_attr, bool):
        if is_empty_attr:
            return None

    try:
        if int(getattr(merged, "pointCount", 0)) <= 0:
            return None
    except Exception:
        pass

    if merged is None:
        return None
    return merged


def _normalize_mask_mode(mask_mode):
    mode = str(mask_mode or "").strip().lower()
    if mode not in ("largest", "union"):
        return "largest"
    return mode


def _extract_sahi_bbox_xyxy(pred):
    try:
        bbox = getattr(pred, "bbox", None)
        if bbox is None:
            return None
        if hasattr(bbox, "to_xyxy"):
            vals = bbox.to_xyxy()
            if vals and len(vals) >= 4:
                return float(vals[0]), float(vals[1]), float(vals[2]), float(vals[3])
        for attrs in (("minx", "miny", "maxx", "maxy"), ("x1", "y1", "x2", "y2")):
            if all(hasattr(bbox, a) for a in attrs):
                return tuple(float(getattr(bbox, a)) for a in attrs)
    except Exception:
        return None
    return None


def _extract_sahi_mask_polygons(pred):
    polys = []
    try:
        mask_obj = getattr(pred, "mask", None)
        if mask_obj is None:
            return polys
        seg = getattr(mask_obj, "segmentation", None)
        if seg is None:
            return polys

        if isinstance(seg, (list, tuple)) and seg and isinstance(seg[0], (int, float)):
            seg = [seg]

        if isinstance(seg, (list, tuple)):
            for item in seg:
                if not isinstance(item, (list, tuple)) or len(item) < 6:
                    continue
                coords = []
                for i in range(0, len(item), 2):
                    try:
                        coords.append((float(item[i]), float(item[i + 1])))
                    except Exception:
                        break
                if len(coords) >= 3:
                    polys.append(coords)
    except Exception:
        return []
    return polys


def _cleanup_tmp_files(folder):
    try:
        exts = (".shp", ".shx", ".dbf", ".prj", ".cpg", ".sbn", ".sbx", ".xml")
        for name in os.listdir(folder):
            if not name.lower().startswith("tmp_"):
                continue
            p = os.path.join(folder, name)
            if os.path.isfile(p):
                try:
                    os.remove(p)
                except Exception:
                    pass
                continue
            base, _ = os.path.splitext(p)
            for ext in exts:
                fp = base + ext
                if os.path.exists(fp):
                    try:
                        os.remove(fp)
                    except Exception:
                        pass
    except Exception:
        pass


def _build_detection_record(pred):
    class_name = ""
    conf = 0.0
    try:
        class_name = str(getattr(getattr(pred, "category", None), "name", "") or "")
    except Exception:
        class_name = ""
    try:
        conf = to_float_scalar(getattr(getattr(pred, "score", None), "value", None), 0.0)
    except Exception:
        conf = 0.0

    bbox = _extract_sahi_bbox_xyxy(pred)
    masks = _extract_sahi_mask_polygons(pred)
    return {
        "class": class_name,
        "confidence": conf,
        "bbox_xyxy": list(bbox) if bbox else None,
        "mask_polygons_px": [[[float(x), float(y)] for (x, y) in poly] for poly in masks],
    }


def _centroid_from_poly(poly_px):
    if not poly_px:
        return None
    try:
        xs = [float(p[0]) for p in poly_px]
        ys = [float(p[1]) for p in poly_px]
        return sum(xs) / len(xs), sum(ys) / len(ys)
    except Exception:
        return None


def _create_outputs_from_json(json_path, output_root, outputs, raster_info, source_name="ortho", mask_mode="largest"):
    if not arcpy:
        raise RuntimeError("arcpy is required to write shapefiles")

    with open(json_path, "r", encoding="utf-8") as f:
        detections = json.load(f)
    if not isinstance(detections, list):
        detections = []

    extent = raster_info.extent
    cell_w = raster_info.cell_x
    cell_h = raster_info.cell_y
    sr = raster_info.spatial_ref
    bounds = (extent.XMin, extent.XMax, extent.YMin, extent.YMax)
    mask_mode = _normalize_mask_mode(mask_mode)

    shp_points = os.path.join(output_root, "Detected_Points.shp")
    shp_bboxes = os.path.join(output_root, "Detected_BBoxes.shp")
    shp_masks = os.path.join(output_root, "Detected_Masks.shp")

    if "point" in outputs:
        create_empty_shapefile(shp_points, "POINT", spatial_ref=sr)
    if "bbox" in outputs:
        create_empty_shapefile(shp_bboxes, "POLYGON", spatial_ref=sr)
    if "mask" in outputs:
        create_empty_shapefile(shp_masks, "POLYGON", spatial_ref=sr)

    for det in detections:
        cls = det.get("class", "")
        conf = to_float_scalar(det.get("confidence", 0.0), 0.0)
        bbox = det.get("bbox_xyxy")
        mask_polys = det.get("mask_polygons_px") or []

        if bbox and len(bbox) == 4 and "bbox" in outputs:
            x1, y1, x2, y2 = [float(v) for v in bbox]
            bbox_coords = _bbox_pixels_to_coords(extent, x1, y1, x2, y2, cell_w, cell_h)
            save_polygon(shp_bboxes, bbox_coords, conf, cls, source_name, "sahi_full", cell_w, cell_h, bounds)

        if "mask" in outputs:
            primary_mask_poly = _select_primary_mask_polygon(mask_polys)
            mask_geom = None
            if mask_mode == "union":
                mask_geom = _build_union_mask_geometry(mask_polys, extent, cell_w, cell_h, sr)
            if mask_geom is None and primary_mask_poly:
                mask_geom = _mask_poly_to_geometry(primary_mask_poly, extent, cell_w, cell_h, sr)
            if mask_geom is not None:
                save_polygon_geometry(shp_masks, mask_geom, conf, cls, source_name, "sahi_full", cell_w, cell_h, bounds)
        else:
            primary_mask_poly = _select_primary_mask_polygon(mask_polys)

        if "point" in outputs:
            cx = cy = None
            if bbox and len(bbox) == 4:
                x1, y1, x2, y2 = [float(v) for v in bbox]
                cx = (x1 + x2) / 2.0
                cy = (y1 + y2) / 2.0
            elif primary_mask_poly:
                c = _centroid_from_poly(primary_mask_poly)
                if c:
                    cx, cy = c
            if cx is not None and cy is not None:
                mx, my = _pixel_to_map(extent, cx, cy, cell_w, cell_h)
                save_point(shp_points, mx, my, conf, cls, source_name, "sahi_full", cell_w, cell_h, bounds)


def run_predictions(tiles_dir, model_path, confidence, outputs, tile_size=640, nms_overlap=0.5, use_sahi=True, mask_mode="largest"):
    if not arcpy:
        raise RuntimeError("arcpy is required to write shapefiles with projection")

    if not use_sahi:
        LOG.warning("WARN: --no-sahi ignored. This version uses SAHI-only pipeline.")

    if not model_path:
        raise RuntimeError("Model path is required")

    output_root = _resolve_detection_root(tiles_dir)
    os.makedirs(output_root, exist_ok=True)
    experiment_name = _build_experiment_name(tile_size, confidence, model_path)
    output_root = os.path.join(output_root, experiment_name)
    os.makedirs(output_root, exist_ok=True)

    ortho_path = _resolve_ortho_from_tiles_dir(tiles_dir)
    if not ortho_path or not os.path.exists(ortho_path):
        raise RuntimeError("Source ortho was not found via Tiles directory (expected Products/Orthos).")

    try:

        from sahi import AutoDetectionModel
        from sahi.predict import get_sliced_prediction
    except Exception as e:
        raise RuntimeError(f"SAHI is required but not available: {e}")

    detection_model = None
    for model_type in ("ultralytics", "yolov8"):
        try:
            detection_model = AutoDetectionModel.from_pretrained(
                model_type=model_type,
                model_path=model_path,
                confidence_threshold=float(confidence),
                device="cuda:0",
            )
            break
        except Exception:
            detection_model = None

    if detection_model is None:
        detection_model = AutoDetectionModel.from_pretrained(
            model_type="yolov8",
            model_path=model_path,
            confidence_threshold=float(confidence),
            device="cpu",
        )

    result = get_sliced_prediction(
        image=str(ortho_path),
        detection_model=detection_model,
        slice_height=int(tile_size),
        slice_width=int(tile_size),
        overlap_height_ratio=0.2,
        overlap_width_ratio=0.2,
        postprocess_type="NMS",
        postprocess_match_metric="IOS",
        postprocess_match_threshold=float(nms_overlap),
    )

    detections = [_build_detection_record(pred) for pred in (getattr(result, "object_prediction_list", []) or [])]

    json_path = os.path.join(output_root, "all_detections_sahi.json")
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(detections, f, ensure_ascii=False, indent=2)

    raster_info = get_raster_info(Path(ortho_path), arcpy)
    _create_outputs_from_json(
        json_path=json_path,
        output_root=output_root,
        outputs=outputs,
        raster_info=raster_info,
        source_name=os.path.basename(ortho_path),
        mask_mode=mask_mode,
    )

    _cleanup_tmp_files(output_root)
    LOG.info("INFO: SAHI detection completed. JSON=%s", json_path)
    return output_root


def parse_args(argv):
    p = argparse.ArgumentParser()
    p.add_argument("--tiles-dir", required=True)
    p.add_argument("--model", required=False)
    p.add_argument("--confidence", type=float, default=0.5)
    p.add_argument("--tile-size", type=int, default=None)
    p.add_argument("--outputs", default=None, help="comma-separated: point,mask,bbox")
    p.add_argument("--mask-mode", default=None, choices=["largest", "union"], help="mask strategy: largest contour only or union all contours")
    p.add_argument("--nms-overlap", type=float, default=None, help="NMS overlap threshold for SAHI postprocess")
    p.add_argument("--use-sahi", action="store_true", help="kept for compatibility; SAHI is always used")
    p.add_argument("--no-sahi", action="store_true", help="kept for compatibility; ignored in SAHI-only mode")
    return p.parse_args(argv)


def manual_args():
    argv_full = [
        "--tiles-dir",
        r"C:\Users\omen_\OneDrive\Documents\ArcGIS\Projects\MyProject2\OrthoMapping\opp2.eomw\Tiles",
        "--model",
        r"U:\PV-SEG\best_pv-seg-yv11x_dsv1\weights\best_pv-seg-yv11x_dsv1.pt",
        "--confidence",
        "0.5",
        "--tile-size",
        "640",
        "--outputs",
        "point,bbox,mask",
    ]
    return parse_args(argv_full)


def main(argv=None):
    if DEBUG:
        args = manual_args()
    else:
        args = parse_args(argv or sys.argv[1:])

    model_path = args.model or str(get_saved_global_setting("model", ""))
    if not model_path:
        model_path = "yolo11x-seg.pt"

    tile_size = args.tile_size if args.tile_size is not None else int(get_saved_global_setting("tile_size", 640))
    nms_overlap = args.nms_overlap if args.nms_overlap is not None else float(get_saved_global_setting("nms_overlap", 0.5))
    mask_mode = _normalize_mask_mode(args.mask_mode if args.mask_mode is not None else get_saved_global_setting("mask_mode", "largest"))

    outputs_text = args.outputs if args.outputs is not None else str(get_saved_global_setting("outputs", "point,bbox,mask"))
    outputs = [o.strip().lower() for o in outputs_text.split(",") if o.strip()]

    set_saved_global_settings(
        {
            "model": model_path,
            "tile_size": int(tile_size),
            "outputs": ",".join(outputs),
            "nms_overlap": float(nms_overlap),
            "mask_mode": mask_mode,
            "use_sahi": True,
        }
    )

    experiment_dir = run_predictions(
        args.tiles_dir,
        model_path,
        args.confidence,
        outputs,
        tile_size=tile_size,
        nms_overlap=nms_overlap,
        use_sahi=True,
        mask_mode=mask_mode,
    )

    if experiment_dir:
        sys.stdout.write(f"EXPERIMENT_DIR={experiment_dir}\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
