"""Typed results. Every timestamp is a timezone-aware UTC datetime."""

from __future__ import annotations

from collections.abc import Iterator
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import Generic, TypeVar
from uuid import UUID

T = TypeVar("T")


class Quality(IntEnum):
    """Whether a Measurement can be trusted; the stored `measurement.quality` code."""

    GOOD = 0
    UNCERTAIN = 1
    BAD = 2


@dataclass(frozen=True, slots=True)
class Plant:
    plant_id: str


@dataclass(frozen=True, slots=True)
class Line:
    plant_id: str
    line_id: str


@dataclass(frozen=True, slots=True)
class Asset:
    asset_id: str
    plant_id: str
    line_id: str
    asset_type: str


@dataclass(frozen=True, slots=True)
class Tag:
    tag_id: int
    plant_id: str
    line_id: str
    asset_id: str
    name: str
    unit: str | None
    min_valid: float | None
    max_valid: float | None
    derived_by: str | None
    """Transformation label that produces this derived Tag; None for raw Tags."""

    @property
    def is_derived(self) -> bool:
        return self.derived_by is not None


@dataclass(frozen=True, slots=True)
class Measurement:
    time: datetime
    """Source timestamp (ADR-0009)."""
    tag_id: int
    plant_id: str
    line_id: str
    asset_id: str
    tag: str
    unit: str | None
    value: float | None
    """None when the stored value is null; independent of `quality`."""
    quality: Quality
    batch_id: UUID
    ingested_at: datetime


@dataclass(frozen=True, slots=True)
class Rollup:
    """Summary of the Good Measurements of one Tag in the minute starting at `bucket`."""

    bucket: datetime
    tag_id: int
    plant_id: str
    line_id: str
    asset_id: str
    tag: str
    unit: str | None
    min_value: float | None
    max_value: float | None
    avg_value: float | None
    sample_count: int


@dataclass(frozen=True, slots=True)
class Gap:
    """A period without Samples because the Bridge lost its source connection."""

    event_id: UUID
    plant_id: str
    line_id: str
    asset_id: str
    endpoint: str
    start: datetime
    end: datetime
    reason: str | None
    occurred_at: datetime


@dataclass(frozen=True, slots=True)
class Page(Generic[T]):
    """At most `limit` items in query order; pass `next_cursor` to read the next page."""

    items: list[T]
    next_cursor: str | None

    @property
    def has_more(self) -> bool:
        return self.next_cursor is not None

    def __iter__(self) -> Iterator[T]:
        return iter(self.items)

    def __len__(self) -> int:
        return len(self.items)


@dataclass(frozen=True, slots=True)
class Capabilities:
    """What the connected database provides, detected at connect."""

    rollups: bool
    """`measurement_1m` exists (migration 001)."""
    gaps: bool
    """`plant_event` exists (migration 002)."""
    derived_tags: bool
    """`tag.derived_by` exists (migration 003)."""
    can_write: bool
    """The login may insert Measurements; a reader login cannot."""
    server_version: str
    timescaledb_version: str | None
