#!/usr/bin/env python
"""
tile_generator.py

Генерация геопривязанных тайлов из орто-растрового изображения и создание сетки .shp

Требования: запуск в окружении ArcGIS Pro (arcpy). Скрипт использует arcpy для извлечения тайлов и создания shapefile сетки.

Аргументы:
  --ortho-image : путь к орто-изображению (в формате, поддерживаемом ArcGIS)
  --tiles-folder: папка Tiles (будут созданы Images и shapes)
  --tile-size   : размер тайла в пикселях (default 640)
  --overlap     : перекрытие в процентах (default 30)

Пример:
  python tile_generator.py --ortho-image "C:\Proj\OrthoMapping\Ortho.tif" --tiles-folder "C:\Proj\OrthoMapping\Ortho\Tiles" --tile-size 640 --overlap 30
"""
import os
import sys
import argparse
from pathlib import Path
from utils import (
    get_logger,
    is_debug_enabled,
    get_raster_info,
    get_saved_global_setting,
    set_saved_global_settings,
    get_saved_project_opp,
    set_saved_project_opp,
)
from models import (
    Paths,
    RasterInfo,
    RunSettings,
    TileExtent,
    TileStats,
)

LOG = get_logger(__name__)

try:
    import arcpy
except Exception as e:
    LOG.error("arcpy is required. Run this script in ArcGIS Pro Python environment.")
    raise

DEBUG = is_debug_enabled("OPP_YOLO_DEBUG_TILE_GENERATOR")


def parse_args(argv):
    p = argparse.ArgumentParser()
    p.add_argument('--ortho-image', required=False)
    p.add_argument('--tiles-folder', required=True)
    p.add_argument('--tile-size', type=int, default=None)
    p.add_argument('--overlap', type=float, default=None)
    return p.parse_args(argv)

def manual_args():
    argv_full = [
        '--ortho-image', r"C:\Users\omen_\OneDrive\Documents\ArcGIS\Projects\MyProject2\OrthoMapping\opp2.eomw\Products\Orthos\opp2_dom.tif",
        '--tiles-folder', r'C:\Users\omen_\OneDrive\Documents\ArcGIS\Projects\MyProject2\OrthoMapping\opp2.eomw\Tiles',
        '--tile-size', '450',
        '--overlap', '30'
    ]
    return parse_args(argv_full)


def create_folders(tiles_folder):
    images = os.path.join(tiles_folder, 'Images')
    shapes = os.path.join(tiles_folder, 'shapes')
    os.makedirs(images, exist_ok=True)
    os.makedirs(shapes, exist_ok=True)
    return images, shapes


def _get_raster_info(in_raster: Path) -> RasterInfo:
    return get_raster_info(in_raster, arcpy)


def _build_tile_extents(settings: RunSettings, raster: RasterInfo) -> tuple[list[TileExtent], int]:
    overlap_px = int(round(settings.pixel_size * settings.overlap_ratio))
    if overlap_px < 0 or overlap_px >= settings.pixel_size:
        raise ValueError("Overlap must be in [0, pixel_size).")

    tile_w = settings.pixel_size * raster.cell_x
    tile_h = settings.pixel_size * raster.cell_y
    step_x = tile_w - overlap_px * raster.cell_x
    step_y = tile_h - overlap_px * raster.cell_y
    if step_x <= 0 or step_y <= 0:
        raise ValueError("Invalid overlap: tile step is <= 0.")

    extents: list[TileExtent] = []
    tile_id = 1
    y = raster.extent.YMin
    eps = 1e-9
    while y < raster.extent.YMax - eps:
        x = raster.extent.XMin
        while x < raster.extent.XMax - eps:
            extents.append(
                TileExtent(
                    tile_id=tile_id,
                    tile_name=f"{settings.out_basename}_{tile_id}",
                    xmin=x,
                    ymin=y,
                    xmax=x + tile_w,
                    ymax=y + tile_h,
                )
            )
            tile_id += 1
            x += step_x
        y += step_y
    return extents, overlap_px


def _create_grid(paths: Paths, raster: RasterInfo, extents: list[TileExtent], progress=None) -> Path:
    grid_fc = paths.tiles_shapes_dir / "Tile_Grid_Pixels.shp"
    prev_add_outputs = getattr(arcpy.env, "addOutputsToMap", True)
    arcpy.env.addOutputsToMap = False
    try:
        if arcpy.Exists(str(grid_fc)):
            arcpy.management.Delete(str(grid_fc))

        arcpy.management.CreateFeatureclass(
            str(paths.tiles_shapes_dir),
            "Tile_Grid_Pixels",
            "POLYGON",
            spatial_reference=raster.spatial_ref,
        )
        arcpy.management.AddField(str(grid_fc), "TileName", "TEXT", field_length=80)
        arcpy.management.AddField(str(grid_fc), "TileId", "LONG")

        total = len(extents)
        with arcpy.da.InsertCursor(str(grid_fc), ["SHAPE@", "TileName", "TileId"]) as cur:
            for idx, tile in enumerate(extents, start=1):
                poly = arcpy.Polygon(
                    arcpy.Array(
                        [
                            arcpy.Point(tile.xmin, tile.ymin),
                            arcpy.Point(tile.xmax, tile.ymin),
                            arcpy.Point(tile.xmax, tile.ymax),
                            arcpy.Point(tile.xmin, tile.ymax),
                            arcpy.Point(tile.xmin, tile.ymin),
                        ]
                    ),
                    raster.spatial_ref,
                )
                cur.insertRow([poly, tile.tile_name, tile.tile_id])
                if progress is not None and (idx % 25 == 0 or idx == total):
                    progress("Create tile grid", idx, total)
        return grid_fc
    finally:
        arcpy.env.addOutputsToMap = prev_add_outputs


def _add_layer_to_map(project: arcpy.mp.ArcGISProject | None, fc_path: Path) -> None:
    if project is None:
        return
    try:
        target_map = project.activeMap
        if target_map is None:
            maps = project.listMaps()
            if not maps:
                return
            target_map = maps[0]
        target_map.addDataFromPath(str(fc_path))
    except Exception:
        return


def _cleanup_old_tiles(images_dir: Path, out_basename: str) -> None:
    patterns = [
        f"{out_basename}*.png",
        f"{out_basename}*.PNG",
        f"{out_basename}*.pgw",
        f"{out_basename}*.PGW",
        f"{out_basename}*.png.aux.xml",
        f"{out_basename}*.PNG.aux.xml",
    ]
    for pattern in patterns:
        for file_path in images_dir.glob(pattern):
            try:
                file_path.unlink(missing_ok=True)
            except Exception:
                continue


def _split_raster_fast(paths: Paths, raster: RasterInfo, settings: RunSettings, overlap_px: int, progress, is_cancelled) -> int:
    arcpy.env.overwriteOutput = True
    prev_add_outputs = getattr(arcpy.env, "addOutputsToMap", True)
    arcpy.env.addOutputsToMap = False
    progress("SplitRaster tiles", 0, 1)
    try:
        if is_cancelled():
            raise RuntimeError("Operation cancelled by user.")

        origin_str = f"{raster.extent.XMin} {raster.extent.YMin}"
        arcpy.management.SplitRaster(
            in_raster=str(paths.in_raster),
            out_folder=str(paths.tiles_images_dir),
            out_base_name=settings.out_basename,
            split_method="SIZE_OF_TILE",
            format="PNG",
            resampling_type="BILINEAR",
            tile_size=f"{settings.pixel_size} {settings.pixel_size}",
            overlap=str(overlap_px),
            units="PIXELS",
            origin=origin_str,
        )

        if is_cancelled():
            raise RuntimeError("Operation cancelled by user.")

        pngs = {p.resolve() for p in paths.tiles_images_dir.glob(f"{settings.out_basename}*.png")}
        pngs.update({p.resolve() for p in paths.tiles_images_dir.glob(f"{settings.out_basename}*.PNG")})
        progress("SplitRaster tiles", 1, 1)
        return len(pngs)
    finally:
        arcpy.env.addOutputsToMap = prev_add_outputs


def run_tiling(project: arcpy.mp.ArcGISProject | None, paths: Paths, settings: RunSettings, progress, is_cancelled) -> tuple[TileStats, list[TileExtent]]:
    progress("Analyze raster", 0, 1)
    raster = _get_raster_info(paths.in_raster)
    extents, overlap_px = _build_tile_extents(settings, raster)

    progress("Create tile grid", 0, max(len(extents), 1))
    grid_fc = _create_grid(paths, raster, extents, progress=progress)
    _add_layer_to_map(project, grid_fc)

    _cleanup_old_tiles(paths.tiles_images_dir, settings.out_basename)
    images_saved = _split_raster_fast(paths, raster, settings, overlap_px, progress, is_cancelled)

    stats = TileStats(
        grid_feature_class=grid_fc,
        total_tiles=len(extents),
        tile_width_px=settings.pixel_size,
        overlap_px=overlap_px,
        cell_x=raster.cell_x,
        cell_y=raster.cell_y,
        images_saved=images_saved,
    )
    return stats, extents



def main():
    if DEBUG:
        args = manual_args()
    else:
        args = parse_args(sys.argv[1:])

    project_dir = Path(args.tiles_folder).parent
    ortho = args.ortho_image or get_saved_project_opp(project_dir)
    tiles_folder = args.tiles_folder
    tile_size = args.tile_size if args.tile_size is not None else int(get_saved_global_setting("tile_size", 640))
    overlap = args.overlap if args.overlap is not None else float(get_saved_global_setting("overlap", 30.0))

    if not ortho:
        LOG.error("Ortho image is not set. Provide --ortho-image or save OPP for this project.")
        sys.exit(2)

    if not os.path.exists(ortho):
        LOG.error("Ortho image not found: %s", ortho)
        sys.exit(2)

    try:
        set_saved_project_opp(project_dir, ortho)
        set_saved_global_settings({
            "tile_size": int(tile_size),
            "overlap": float(overlap),
        })
    except Exception:
        pass

    os.makedirs(tiles_folder, exist_ok=True)

    # Use local run_tiling implementation (adapted from src/pipeline.py), without importing src.pipeline
    try:
        project_obj = None
        try:
            project_obj = arcpy.mp.ArcGISProject("CURRENT")
        except Exception:
            try:
                project_obj = arcpy.mp.ArcGISProject()
            except Exception:
                project_obj = None

        paths = Paths(
            project_dir=Path(tiles_folder).parent,
            in_raster=Path(ortho),
            tiles_dir=Path(tiles_folder),
            tiles_images_dir=Path(tiles_folder) / "Images",
            tiles_shapes_dir=Path(tiles_folder) / "shapes"
        )
        os.makedirs(paths.tiles_images_dir, exist_ok=True)
        os.makedirs(paths.tiles_shapes_dir, exist_ok=True)

        settings = RunSettings(pixel_size=tile_size, overlap_percent=overlap, out_basename=f'{Path(ortho).stem}_tile')

        def progress_cb(msg: str, cur: int, total: int) -> None:
            try:
                arcpy.AddMessage(f"{msg} ({cur}/{total})")
            except Exception:
                LOG.info("%s (%s/%s)", msg, cur, total)

        def is_cancelled_cb() -> bool:
            return False

        LOG.info("Starting local run_tiling")
        tile_stats, extents = run_tiling(project_obj, paths, settings, progress_cb, is_cancelled_cb)
        LOG.info("Tiling finished: total_tiles=%s, images_saved=%s", getattr(tile_stats, 'total_tiles', None), getattr(tile_stats, 'images_saved', None))
        sys.exit(0)
    except Exception as e:
        LOG.exception("Local run_tiling failed: %s", e)
        sys.exit(4)


if __name__ == '__main__':
    main()
