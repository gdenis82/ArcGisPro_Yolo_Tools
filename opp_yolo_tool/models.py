from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass(slots=True)
class Paths:
    project_dir: Path
    in_raster: Path
    tiles_dir: Path
    tiles_images_dir: Path
    tiles_shapes_dir: Path
    detection_dir: Path = field(init=False)


@dataclass(slots=True)
class RunSettings:
    pixel_size: int = 640
    overlap_percent: float = 30.0
    full_process: bool = False
    model_name: str = "yolo11x-seg.pt"
    confidence_threshold: float = 0.5
    out_basename: str = "ortho_tile"
    generate_points: bool = True
    generate_bboxes: bool = True
    generate_masks: bool = True

    @property
    def overlap_ratio(self) -> float:
        return self.overlap_percent / 100.0


@dataclass(slots=True)
class TileExtent:
    tile_id: int
    tile_name: str
    xmin: float
    ymin: float
    xmax: float
    ymax: float


@dataclass(slots=True)
class TileStats:
    grid_feature_class: Path
    total_tiles: int
    tile_width_px: int
    overlap_px: int
    cell_x: float
    cell_y: float
    images_saved: int = 0


@dataclass(slots=True)
class DetectionStats:
    images_processed: int = 0
    tiles_with_detections: int = 0
    total_objects: int = 0
    detections_json: Path | None = None
    detections_geo_json: Path | None = None
    overlays_dir: Path | None = None
    layers_created: list[Path] = field(default_factory=list)


@dataclass(slots=True)
class RasterInfo:
    cols: int
    rows: int
    cell_x: float
    cell_y: float
    extent: Any
    spatial_ref: Any


@dataclass(slots=True)
class ModelInfo:
    name: str
    task: str | None = None
    names: dict | list | None = None
