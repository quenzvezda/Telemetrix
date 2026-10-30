"""The read-only query client (ADR-0020, ADR-0021)."""

from __future__ import annotations

import math
import warnings
from collections.abc import Callable, Iterable, Iterator, Sequence
from dataclasses import dataclass
from typing import Any, Generic, TypeVar

import psycopg
from psycopg import errors as pg

from . import _cursor, _queries
from ._time import TimeArg, interval, utc
from .errors import (ClientClosedError, ConnectionFailedError, FeatureUnavailableError, InsecureRoleWarning,
                     PermissionDeniedError, QueryCancelledError, QueryTimeoutError, SchemaMismatchError,
                     TelemetrixQueryError)
from .models import Asset, Capabilities, Gap, Line, Measurement, Page, Plant, Quality, Rollup, Tag
from .settings import ConnectionSettings

T = TypeVar("T")

READER_ROLE = "telemetrix_reader"
_SESSION_OPTIONS = "-c default_transaction_read_only=on -c TimeZone=UTC"


def connect(settings: ConnectionSettings | str | None = None) -> TelemetrixClient:
    """Open a read-only client.

    With no argument, settings come from the environment (`ConnectionSettings.from_env`).
    A string is used as libpq conninfo, with the timeout taken from the environment.
    """
    if settings is None:
        settings = ConnectionSettings.from_env()
    elif isinstance(settings, str):
        settings = ConnectionSettings.from_env(conninfo=settings)
    elif not isinstance(settings, ConnectionSettings):
        raise TypeError("settings must be ConnectionSettings, a conninfo string or None")
    kwargs: dict[str, Any] = {"connect_timeout": max(2, math.ceil(settings.connect_timeout)),
                              "application_name": settings.application_name, "options": _SESSION_OPTIONS}
    if settings.password is not None:
        kwargs["password"] = settings.password
    try:
        connection = psycopg.connect(settings.conninfo, autocommit=True, **kwargs)
    except psycopg.Error as error:
        raise ConnectionFailedError(
            f"cannot connect to {settings.describe()}: {_message(error, settings)}. Check "
            f"TELEMETRIX_DATABASE_URL or the PG* variables, the password source and that the database is running"
        ) from None
    try:
        capabilities = _detect(connection, settings)
    except BaseException:
        connection.close()
        raise
    return TelemetrixClient(connection, settings, capabilities)


class TelemetrixClient:
    """One read-only connection. Not thread-safe, except `cancel()`. Use as a context manager."""

    def __init__(self, connection: psycopg.Connection[Any], settings: ConnectionSettings,
                 capabilities: Capabilities) -> None:
        self._connection = connection
        self._settings = settings
        self._capabilities = capabilities

    @property
    def capabilities(self) -> Capabilities:
        return self._capabilities

    @property
    def settings(self) -> ConnectionSettings:
        return self._settings

    @property
    def closed(self) -> bool:
        return self._connection.closed

    def close(self) -> None:
        self._connection.close()

    def __enter__(self) -> TelemetrixClient:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def cancel(self) -> None:
        """Cancel the running statement, if any; safe to call from another thread."""
        if not self._connection.closed:
            self._connection.cancel_safe()

    # Hierarchy and Tags

    def plants(self, *, timeout: float | None = None) -> list[Plant]:
        return [Plant(*row) for row in self._run(_queries.plants(), timeout)]

    def lines(self, *, plant_id: str | None = None, timeout: float | None = None) -> list[Line]:
        return [Line(*row) for row in self._run(_queries.lines(plant_id), timeout)]

    def assets(self, *, plant_id: str | None = None, line_id: str | None = None, asset_type: str | None = None,
               timeout: float | None = None) -> list[Asset]:
        return [Asset(*row) for row in self._run(_queries.assets(plant_id, line_id, asset_type), timeout)]

    def tags(self, *, plant_id: str | None = None, line_id: str | None = None, asset_id: str | None = None,
             names: str | Iterable[str] | None = None, derived: bool | None = None,
             timeout: float | None = None) -> list[Tag]:
        """Tags with unit and valid range; `derived=True` only derived Tags, `False` only raw ones."""
        sel = _queries.selection(plant_id, line_id, asset_id, names)
        statement = _queries.tags(sel, derived, self._capabilities.derived_tags)
        if derived and not self._capabilities.derived_tags:
            return []
        return [Tag(*row) for row in self._run(statement, timeout)]

    # Measurements

    def measurements(self, start: TimeArg, end: TimeArg, *, plant_id: str | None = None, line_id: str | None = None,
                     asset_id: str | None = None, tags: str | Iterable[str] | None = None,
                     tag_ids: int | Iterable[int] | None = None,
                     qualities: Quality | int | str | Iterable[Quality | int | str] | None = None,
                     limit: int = _queries.DEFAULT_LIMIT, cursor: str | None = None,
                     timeout: float | None = None) -> Page[Measurement]:
        """Measurements with `start <= time < end`, ordered by time and Tag id."""
        query = self._measurement_query(start, end, plant_id, line_id, asset_id, tags, tag_ids, qualities)
        return self._page(query, _queries.limit(limit), cursor, timeout)

    def iter_measurements(self, start: TimeArg, end: TimeArg, *, plant_id: str | None = None,
                          line_id: str | None = None, asset_id: str | None = None,
                          tags: str | Iterable[str] | None = None, tag_ids: int | Iterable[int] | None = None,
                          qualities: Quality | int | str | Iterable[Quality | int | str] | None = None,
                          page_size: int = _queries.DEFAULT_LIMIT,
                          timeout: float | None = None) -> Iterator[Measurement]:
        """Every matching Measurement, read one page at a time."""
        query = self._measurement_query(start, end, plant_id, line_id, asset_id, tags, tag_ids, qualities)
        return self._iterate(query, _queries.limit(page_size, "page_size"), timeout)

    # Rollups

    def rollups(self, start: TimeArg, end: TimeArg, *, plant_id: str | None = None, line_id: str | None = None,
                asset_id: str | None = None, tags: str | Iterable[str] | None = None,
                tag_ids: int | Iterable[int] | None = None, limit: int = _queries.DEFAULT_LIMIT,
                cursor: str | None = None, timeout: float | None = None) -> Page[Rollup]:
        """One-minute Rollups of Good Measurements with `start <= bucket < end`."""
        query = self._rollup_query(start, end, plant_id, line_id, asset_id, tags, tag_ids)
        return self._page(query, _queries.limit(limit), cursor, timeout)

    def iter_rollups(self, start: TimeArg, end: TimeArg, *, plant_id: str | None = None,
                     line_id: str | None = None, asset_id: str | None = None,
                     tags: str | Iterable[str] | None = None, tag_ids: int | Iterable[int] | None = None,
                     page_size: int = _queries.DEFAULT_LIMIT, timeout: float | None = None) -> Iterator[Rollup]:
        query = self._rollup_query(start, end, plant_id, line_id, asset_id, tags, tag_ids)
        return self._iterate(query, _queries.limit(page_size, "page_size"), timeout)

    # Gaps

    def gaps(self, start: TimeArg, end: TimeArg, *, plant_id: str | None = None, line_id: str | None = None,
             asset_id: str | None = None, limit: int = _queries.DEFAULT_LIMIT, cursor: str | None = None,
             timeout: float | None = None) -> Page[Gap]:
        """Gaps whose span overlaps `start <= t < end`, ordered by Gap start."""
        query = self._gap_query(start, end, plant_id, line_id, asset_id)
        return self._page(query, _queries.limit(limit), cursor, timeout)

    def iter_gaps(self, start: TimeArg, end: TimeArg, *, plant_id: str | None = None, line_id: str | None = None,
                  asset_id: str | None = None, page_size: int = _queries.DEFAULT_LIMIT,
                  timeout: float | None = None) -> Iterator[Gap]:
        query = self._gap_query(start, end, plant_id, line_id, asset_id)
        return self._iterate(query, _queries.limit(page_size, "page_size"), timeout)

    # Paged query plumbing

    def _measurement_query(self, start: TimeArg, end: TimeArg, plant_id: str | None, line_id: str | None,
                           asset_id: str | None, tags: Any, tag_ids: Any, qualities: Any) -> _PagedQuery[Measurement]:
        utc_start, utc_end = interval(start, end)
        sel = _queries.selection(plant_id, line_id, asset_id, tags, tag_ids)
        quality = _queries.qualities(qualities)
        filters = {**sel.as_filters(), "start": utc_start, "end": utc_end, "qualities": quality}
        return _PagedQuery(
            _cursor.fingerprint("measurements", filters),
            lambda after, size: _queries.measurements(sel, quality, utc_start, utc_end, after, size),
            _measurement, lambda m: (m.time, m.tag_id), (_cursor.parse_datetime, _cursor.parse_int))

    def _rollup_query(self, start: TimeArg, end: TimeArg, plant_id: str | None, line_id: str | None,
                      asset_id: str | None, tags: Any, tag_ids: Any) -> _PagedQuery[Rollup]:
        self._require(self._capabilities.rollups, "Rollups", "001_rollups_and_retention.sql")
        utc_start, utc_end = interval(start, end)
        sel = _queries.selection(plant_id, line_id, asset_id, tags, tag_ids)
        filters = {**sel.as_filters(), "start": utc_start, "end": utc_end}
        return _PagedQuery(
            _cursor.fingerprint("rollups", filters),
            lambda after, size: _queries.rollups(sel, utc_start, utc_end, after, size),
            _rollup, lambda r: (r.bucket, r.tag_id), (_cursor.parse_datetime, _cursor.parse_int))

    def _gap_query(self, start: TimeArg, end: TimeArg, plant_id: str | None, line_id: str | None,
                   asset_id: str | None) -> _PagedQuery[Gap]:
        self._require(self._capabilities.gaps, "Gaps", "002_plant_events.sql")
        utc_start, utc_end = interval(start, end)
        sel = _queries.selection(plant_id, line_id, asset_id)
        filters = {**sel.as_filters(), "start": utc_start, "end": utc_end}
        return _PagedQuery(
            _cursor.fingerprint("gaps", filters),
            lambda after, size: _queries.gaps(sel, utc_start, utc_end, after, size),
            _gap, lambda g: (g.start, str(g.event_id)), (_cursor.parse_datetime, str))

    def _page(self, query: _PagedQuery[T], size: int, cursor: str | None, timeout: float | None) -> Page[T]:
        after = None if cursor is None else _cursor.decode(cursor, query.fingerprint, query.parsers)
        return self._fetch(query, after, size, timeout)

    def _iterate(self, query: _PagedQuery[T], size: int, timeout: float | None) -> Iterator[T]:
        self._ensure_open()

        def pages() -> Iterator[T]:
            after: tuple[Any, ...] | None = None
            while True:
                page = self._fetch(query, after, size, timeout)
                yield from page.items
                if not page.has_more:
                    return
                after = query.key(page.items[-1])

        return pages()

    def _fetch(self, query: _PagedQuery[T], after: tuple[Any, ...] | None, size: int,
               timeout: float | None) -> Page[T]:
        rows = self._run(query.statement(after, size), timeout)
        items = [query.convert(row) for row in rows[:size]]
        next_cursor = _cursor.encode(query.fingerprint, query.key(items[-1])) if len(rows) > size else None
        return Page(items, next_cursor)

    def _require(self, available: bool, feature: str, migration: str) -> None:
        if not available:
            raise FeatureUnavailableError(
                f"{feature} are not available in this database; review and apply "
                f"deploy/sql/migrations/{migration}, then reconnect")

    def _ensure_open(self) -> None:
        if self._connection.closed:
            raise ClientClosedError("the client is closed; call telemetrix_query.connect() again")

    def _run(self, statement: _queries.Statement, timeout: float | None) -> list[tuple[Any, ...]]:
        self._ensure_open()
        milliseconds = _timeout_ms(self._settings.query_timeout if timeout is None else timeout)
        sql, params = statement
        try:
            with self._connection.transaction(), self._connection.cursor() as cursor:
                cursor.execute("SELECT set_config('statement_timeout', %s, true)", (f"{milliseconds}ms",))
                cursor.execute(sql, params)  # type: ignore[arg-type]
                return cursor.fetchall()
        except psycopg.Error as error:
            raise _translate(error, self._connection, self._settings, milliseconds) from None


@dataclass(frozen=True, slots=True)
class _PagedQuery(Generic[T]):
    """A keyset-paged query: its cursor fingerprint, statement per page, row mapping and sort key."""

    fingerprint: str
    statement: Callable[[tuple[Any, ...] | None, int], _queries.Statement]
    convert: Callable[[tuple[Any, ...]], T]
    key: Callable[[T], tuple[Any, ...]]
    parsers: Sequence[Callable[[Any], Any]]


def _measurement(row: tuple[Any, ...]) -> Measurement:
    time, tag_id, plant, line, asset, tag, unit, value, quality, batch_id, ingested_at = row
    return Measurement(utc(time), tag_id, plant, line, asset, tag, unit, value, Quality(quality), batch_id,
                       utc(ingested_at))


def _rollup(row: tuple[Any, ...]) -> Rollup:
    bucket, tag_id, plant, line, asset, tag, unit, minimum, maximum, average, count = row
    return Rollup(utc(bucket), tag_id, plant, line, asset, tag, unit, minimum, maximum, average, int(count))


def _gap(row: tuple[Any, ...]) -> Gap:
    event_id, plant, line, asset, endpoint, start, end, reason, occurred_at = row
    return Gap(event_id, plant, line, asset, endpoint, utc(start), utc(end), reason, utc(occurred_at))


def _detect(connection: psycopg.Connection[Any], settings: ConnectionSettings) -> Capabilities:
    try:
        row = connection.execute(_queries.CAPABILITIES).fetchone()
    except psycopg.Error as error:
        raise _translate(error, connection, settings, None) from None
    assert row is not None
    asset, tag, measurement, rollups, gaps, derived, can_write, server_version, timescaledb, user = row
    missing = [name for name, present in (("asset", asset), ("tag", tag), ("measurement", measurement)) if not present]
    if missing:
        raise SchemaMismatchError(
            f"{settings.describe()} has no {', '.join(missing)} table in schema public; connect to the Telemetrix "
            f"database (deploy/sql/init.sql) or check the dbname")
    if can_write:
        warnings.warn(InsecureRoleWarning(
            f"login {user!r} can write telemetry; the SDK session is read-only, but use a login in the "
            f"{READER_ROLE} role (deploy/sql/migrations/004_query_reader.sql) for analysis"), stacklevel=3)
    return Capabilities(rollups, gaps, derived, can_write, server_version, timescaledb)


def _translate(error: psycopg.Error, connection: psycopg.Connection[Any], settings: ConnectionSettings,
               milliseconds: int | None) -> TelemetrixQueryError:
    message = _message(error, settings)
    if isinstance(error, pg.QueryCanceled):
        if "statement timeout" in message:
            return QueryTimeoutError(
                f"query exceeded its timeout of {milliseconds / 1000:g} s; narrow the interval or filters, "
                f"use a smaller page, or pass a larger timeout")
        return QueryCancelledError("query was cancelled")
    if isinstance(error, pg.InsufficientPrivilege):
        return PermissionDeniedError(
            f"{message}. Grant the login read access: apply deploy/sql/migrations/004_query_reader.sql and run "
            f"GRANT {READER_ROLE} TO <login>")
    if isinstance(error, (pg.UndefinedTable, pg.UndefinedColumn, pg.UndefinedFunction)):
        return SchemaMismatchError(f"{message}; the database schema does not match this telemetrix-query version")
    if connection.broken or isinstance(error, psycopg.OperationalError):
        return ConnectionFailedError(
            f"connection to {settings.describe()} failed: {message}. Create a new client with connect()")
    return TelemetrixQueryError(f"query failed: {message}")


def _message(error: psycopg.Error, settings: ConnectionSettings) -> str:
    message = (error.diag.message_primary if error.diag and error.diag.message_primary else str(error)).strip()
    if settings.password:
        message = message.replace(settings.password, "***")
    return message


def _timeout_ms(seconds: float) -> int:
    if not isinstance(seconds, (int, float)) or isinstance(seconds, bool) or not math.isfinite(seconds) or seconds <= 0:
        raise ValueError(f"timeout must be a positive number of seconds, got {seconds!r}")
    return max(1, math.ceil(seconds * 1000))
