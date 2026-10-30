"""Interval arguments: timezone-aware, converted to UTC, half-open (ADR-0020)."""

from __future__ import annotations

from datetime import datetime, timezone

TimeArg = datetime | str


def to_utc(value: TimeArg, name: str) -> datetime:
    if isinstance(value, str):
        try:
            value = datetime.fromisoformat(value)
        except ValueError:
            raise ValueError(f"{name} is not an ISO-8601 timestamp: {value!r}") from None
    elif not isinstance(value, datetime):
        raise TypeError(f"{name} must be a datetime or an ISO-8601 string, got {type(value).__name__}")
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError(
            f"{name} has no time zone; pass an aware datetime such as "
            f"datetime(2026, 10, 1, tzinfo=timezone.utc) or a string with an offset such as '2026-10-01T00:00:00Z'")
    return value.astimezone(timezone.utc)


def interval(start: TimeArg, end: TimeArg) -> tuple[datetime, datetime]:
    """The UTC interval `start <= t < end`; `end` must be after `start`."""
    utc_start, utc_end = to_utc(start, "start"), to_utc(end, "end")
    if utc_end <= utc_start:
        raise ValueError(f"end ({utc_end.isoformat()}) must be after start ({utc_start.isoformat()})")
    return utc_start, utc_end


def utc(value: datetime) -> datetime:
    """Normalise a timestamp returned by the database to `datetime.timezone.utc`."""
    return value.astimezone(timezone.utc)
