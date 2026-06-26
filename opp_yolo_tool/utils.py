import logging
import os
import json
import sys
from pathlib import Path

from models import RasterInfo


SETTINGS_FILE = Path.home() / ".opp_yolo_user_settings.json"


def get_logger(name: str) -> logging.Logger:
    logger = logging.getLogger(name)
    if not logging.getLogger().handlers:
        logging.basicConfig(
            level=logging.INFO,
            format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
            stream=sys.stdout,
        )
    return logger


def is_debug_enabled(env_var_name: str) -> bool:
    return os.environ.get(env_var_name, "").strip().lower() in {"1", "true", "yes"}


def to_float_scalar(value, default: float = 0.0) -> float:
    try:
        if value is None:
            return float(default)
        if hasattr(value, "item"):
            value = value.item()
        return float(value)
    except Exception:
        return float(default)


def to_int_scalar(value, default: int = 0) -> int:
    try:
        if value is None:
            return int(default)
        if hasattr(value, "item"):
            value = value.item()
        return int(value)
    except Exception:
        return int(default)


def get_raster_info(in_raster: Path, arcpy_module) -> RasterInfo:
    ras = arcpy_module.Raster(str(in_raster))
    desc = arcpy_module.Describe(str(in_raster))
    return RasterInfo(
        cols=int(ras.width),
        rows=int(ras.height),
        cell_x=float(ras.meanCellWidth),
        cell_y=float(ras.meanCellHeight),
        extent=desc.extent,
        spatial_ref=desc.spatialReference,
    )


def _normalize_project_key(project_dir: Path | str | None) -> str:
    if project_dir is None:
        return ""
    try:
        return str(Path(project_dir).resolve()).lower().replace("/", "\\")
    except Exception:
        return str(project_dir).strip().lower().replace("/", "\\")


def load_user_settings() -> dict:
    try:
        if SETTINGS_FILE.exists():
            with SETTINGS_FILE.open("r", encoding="utf-8") as f:
                data = json.load(f)
                if isinstance(data, dict):
                    data.setdefault("global", {})
                    data.setdefault("projects", {})
                    return data
    except Exception:
        pass
    return {"global": {}, "projects": {}}


def save_user_settings(settings: dict) -> None:
    try:
        SETTINGS_FILE.parent.mkdir(parents=True, exist_ok=True)
        with SETTINGS_FILE.open("w", encoding="utf-8") as f:
            json.dump(settings, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def get_saved_global_setting(name: str, default=None):
    settings = load_user_settings()
    return settings.get("global", {}).get(name, default)


def set_saved_global_settings(values: dict) -> None:
    settings = load_user_settings()
    glob = settings.setdefault("global", {})
    for k, v in values.items():
        glob[k] = v
    save_user_settings(settings)


def get_saved_project_opp(project_dir: Path | str, default: str | None = None) -> str | None:
    key = _normalize_project_key(project_dir)
    if not key:
        return default
    settings = load_user_settings()
    return settings.get("projects", {}).get(key, {}).get("opp", default)


def set_saved_project_opp(project_dir: Path | str, opp_path: Path | str) -> None:
    key = _normalize_project_key(project_dir)
    if not key:
        return
    settings = load_user_settings()
    projects = settings.setdefault("projects", {})
    project_data = projects.setdefault(key, {})
    project_data["opp"] = str(opp_path)
    save_user_settings(settings)
