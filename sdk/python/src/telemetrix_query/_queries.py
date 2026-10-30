"""SQL the SDK runs. Statement text is fixed here; every argument is a bound parameter."""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import asdict, dataclass
from datetime import datetime
from typing import Any

from .models import Quality

DEFAULT_LIMIT = 10_000
MAX_LIMIT = 100_000

Statement = tuple[str, dict[str, Any]]


@dataclass(frozen=True)
class Selection:
    """Hierarchy and Tag filters; every given filter must match (AND)."""

    plant_id: str | None = None
    line_id: str | None = None
    asset_id: str | None = None
    tags: tuple[str, ...] | None = None
    tag_ids: tuple[int, ...] | None = None

    def as_filters(self) -> dict[str, Any]:
        return {key: value for key, value in asdict(self).items() if value is not None}


def selection(plant_id: str | None = None, line_id: str | None = None, asset_id: str | None = None,
              tags: str | Iterable[str] | None = None, tag_ids: int | Iterable[int] | None = None) -> Selection:
    return Selection(_identifier(plant_id, "plant_id"), _identifier(line_id, "line_id"),
                     _identifier(asset_id, "asset_id"), _names(tags, "tags"), _ids(tag_ids))


def qualities(values: Quality | int | str | Iterable[Quality | int | str] | None) -> tuple[int, ...] | None:
    if values is None:
        return None
    if isinstance(values, (Quality, int, str)):
        values = [values]
    result: set[int] = set()
    for value in values:
        if isinstance(value, str):
            try:
                result.add(Quality[value.strip().upper()].value)
            except KeyError:
                raise ValueError(f"unknown Quality {value!r}; use good, uncertain or bad") from None
        elif isinstance(value, int) and not isinstance(value, bool):
            result.add(Quality(value).value)
        else:
            raise TypeError(f"qualities must be Quality values, got {type(value).__name__}")
    if not result:
        raise ValueError("qualities must not be empty; pass None for every Quality")
    return tuple(sorted(result))


def limit(value: int, name: str = "limit") -> int:
    if not isinstance(value, int) or isinstance(value, bool):
        raise TypeError(f"{name} must be an int, got {type(value).__name__}")
    if not 1 <= value <= MAX_LIMIT:
        raise ValueError(f"{name} must be between 1 and {MAX_LIMIT:,}, got {value:,}")
    return value


def plants() -> Statement:
    return "SELECT DISTINCT plant_id FROM public.asset ORDER BY plant_id", {}


def lines(plant_id: str | None) -> Statement:
    params = {"plant_id": _identifier(plant_id, "plant_id")}
    where = " WHERE plant_id = %(plant_id)s" if params["plant_id"] is not None else ""
    return f"SELECT DISTINCT plant_id, line_id FROM public.asset{where} ORDER BY plant_id, line_id", params


def assets(plant_id: str | None, line_id: str | None, asset_type: str | None) -> Statement:
    params = {"plant_id": _identifier(plant_id, "plant_id"), "line_id": _identifier(line_id, "line_id"),
              "asset_type": _identifier(asset_type, "asset_type")}
    conditions = [f"{column} = %({column})s" for column, value in params.items() if value is not None]
    return ("SELECT asset_id, plant_id, line_id, asset_type FROM public.asset"
            + _where(conditions) + " ORDER BY asset_id"), params


def tags(sel: Selection, derived: bool | None, has_derived_column: bool) -> Statement:
    if derived is not None and not isinstance(derived, bool):
        raise TypeError("derived must be True, False or None")
    conditions, params = _hierarchy(sel, measurement_alias=None)
    if derived is not None and has_derived_column:
        conditions.append("t.derived_by IS NOT NULL" if derived else "t.derived_by IS NULL")
    derived_by = "t.derived_by" if has_derived_column else "NULL::text"
    return ("SELECT t.tag_id, a.plant_id, a.line_id, t.asset_id, t.name, t.unit, t.min_valid, t.max_valid, "
            f"{derived_by} FROM public.tag t JOIN public.asset a ON a.asset_id = t.asset_id"
            + _where(conditions) + " ORDER BY t.asset_id, t.name"), params


def measurements(sel: Selection, quality: tuple[int, ...] | None, start: datetime, end: datetime,
                 after: tuple[datetime, int] | None, page_limit: int) -> Statement:
    conditions, params = _hierarchy(sel, measurement_alias="m")
    conditions[:0] = ["m.time >= %(start)s", "m.time < %(end)s"]
    params.update(start=start, end=end, limit=page_limit + 1)
    if quality is not None:
        conditions.append("m.quality = ANY(%(qualities)s::smallint[])")
        params["qualities"] = list(quality)
    if after is not None:
        conditions.append("(m.time, m.tag_id) > (%(after_time)s, %(after_id)s)")
        params.update(after_time=after[0], after_id=after[1])
    return ("SELECT m.time, m.tag_id, a.plant_id, a.line_id, t.asset_id, t.name, t.unit, "
            "m.value, m.quality, m.batch_id, m.ingested_at FROM public.measurement m "
            "JOIN public.tag t ON t.tag_id = m.tag_id JOIN public.asset a ON a.asset_id = t.asset_id"
            + _where(conditions) + " ORDER BY m.time, m.tag_id LIMIT %(limit)s"), params


def rollups(sel: Selection, start: datetime, end: datetime, after: tuple[datetime, int] | None,
            page_limit: int) -> Statement:
    conditions, params = _hierarchy(sel, measurement_alias="r")
    conditions[:0] = ["r.bucket >= %(start)s", "r.bucket < %(end)s"]
    params.update(start=start, end=end, limit=page_limit + 1)
    if after is not None:
        conditions.append("(r.bucket, r.tag_id) > (%(after_time)s, %(after_id)s)")
        params.update(after_time=after[0], after_id=after[1])
    return ("SELECT r.bucket, r.tag_id, a.plant_id, a.line_id, t.asset_id, t.name, t.unit, "
            "r.min_value, r.max_value, r.avg_value, r.sample_count FROM public.measurement_1m r "
            "JOIN public.tag t ON t.tag_id = r.tag_id JOIN public.asset a ON a.asset_id = t.asset_id"
            + _where(conditions) + " ORDER BY r.bucket, r.tag_id LIMIT %(limit)s"), params


def gaps(sel: Selection, start: datetime, end: datetime, after: tuple[datetime, str] | None,
         page_limit: int) -> Statement:
    if sel.tags is not None or sel.tag_ids is not None:
        raise ValueError("Gaps belong to an Asset, not a Tag; filter by plant_id, line_id or asset_id")
    params: dict[str, Any] = {"start": start, "end": end, "limit": page_limit + 1}
    conditions = ["e.event_type = 'Gap'", "e.gap_start IS NOT NULL", "e.gap_end IS NOT NULL",
                  "e.gap_start < %(end)s", "e.gap_end > %(start)s"]
    for column in ("plant_id", "line_id", "asset_id"):
        value = getattr(sel, column)
        if value is not None:
            conditions.append(f"e.{column} = %({column})s")
            params[column] = value
    if after is not None:
        conditions.append("(e.gap_start, e.event_id) > (%(after_time)s, %(after_id)s::uuid)")
        params.update(after_time=after[0], after_id=after[1])
    return ("SELECT e.event_id, e.plant_id, e.line_id, e.asset_id, e.endpoint, e.gap_start, e.gap_end, "
            "e.reason, e.occurred_at FROM public.plant_event e"
            + _where(conditions) + " ORDER BY e.gap_start, e.event_id LIMIT %(limit)s"), params


CAPABILITIES = (
    "SELECT to_regclass('public.asset') IS NOT NULL, to_regclass('public.tag') IS NOT NULL, "
    "to_regclass('public.measurement') IS NOT NULL, to_regclass('public.measurement_1m') IS NOT NULL, "
    "to_regclass('public.plant_event') IS NOT NULL, "
    "EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass('public.tag') "
    "AND attname = 'derived_by' AND NOT attisdropped), "
    "CASE WHEN to_regclass('public.measurement') IS NULL THEN false "
    "ELSE has_table_privilege('public.measurement', 'INSERT') END, "
    "current_setting('server_version'), "
    "(SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'), current_user")


def _hierarchy(sel: Selection, measurement_alias: str | None) -> tuple[list[str], dict[str, Any]]:
    conditions: list[str] = []
    params: dict[str, Any] = {}
    for column, alias in (("plant_id", "a"), ("line_id", "a"), ("asset_id", "t")):
        value = getattr(sel, column)
        if value is not None:
            conditions.append(f"{alias}.{column} = %({column})s")
            params[column] = value
    if sel.tags is not None:
        conditions.append("t.name = ANY(%(tags)s::text[])")
        params["tags"] = list(sel.tags)
    if sel.tag_ids is not None:
        conditions.append(f"{measurement_alias or 't'}.tag_id = ANY(%(tag_ids)s::int[])")
        params["tag_ids"] = list(sel.tag_ids)
    return conditions, params


def _where(conditions: list[str]) -> str:
    return " WHERE " + " AND ".join(conditions) if conditions else ""


def _identifier(value: str | None, name: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string, got {type(value).__name__}")
    if not value:
        raise ValueError(f"{name} must not be empty; pass None to match every value")
    return value


def _names(values: str | Iterable[str] | None, name: str) -> tuple[str, ...] | None:
    if values is None:
        return None
    if isinstance(values, str):
        values = [values]
    result = tuple(sorted({_identifier(value, name) for value in values}))  # type: ignore[type-var]
    if not result:
        raise ValueError(f"{name} must not be empty; pass None to match every Tag")
    return result  # type: ignore[return-value]


def _ids(values: int | Iterable[int] | None) -> tuple[int, ...] | None:
    if values is None:
        return None
    if isinstance(values, int) and not isinstance(values, bool):
        values = [values]
    result: set[int] = set()
    for value in values:  # type: ignore[union-attr]
        if not isinstance(value, int) or isinstance(value, bool):
            raise TypeError(f"tag_ids must be ints, got {type(value).__name__}")
        result.add(value)
    if not result:
        raise ValueError("tag_ids must not be empty; pass None to match every Tag")
    return tuple(sorted(result))
