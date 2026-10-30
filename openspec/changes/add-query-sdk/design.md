# Design

## Context

Stored telemetry lives in four places: `asset` and `tag` (initial schema, `tag.derived_by` from migration 003), the `measurement` hypertable (unique `(tag_id, time)`, nullable `value`, `quality` 0/1/2, `batch_id`, `ingested_at`), the continuous aggregate `measurement_1m` (migration 001, Good only, 30-day raw / 1-year rollup retention) and `plant_event` (migration 002, Gaps with `gap_start` / `gap_end`). Migrations are optional and applied explicitly, so a consumer cannot assume all of them exist. The issue asks for a supported package that hides this.

## Goals / Non-Goals

**Goals:**
- One documented Python API for the hierarchy, Tags, Measurements, Rollups and Gaps.
- Predictable time semantics, bounded memory for large results, explicit errors.
- Read-only by default, with no secret in the repository.

**Non-Goals:**
- A .NET package (deferred, ADR-0019), PyPI publication, an HTTP query service.
- Writing data, raw SQL through the API, transformation lineage queries (issue #13), dead-letter inspection (issue #11).
- Connection pooling, async API, pandas as a dependency.

## Decisions

### Package (ADR-0019)
`sdk/python/` with `pyproject.toml` (setuptools, `src/` layout), distribution `telemetrix-query` 0.1.0, import `telemetrix_query`, Python ≥ 3.11, dependency `psycopg[binary]>=3.2,<4`. Modules: `settings` (connection settings), `client` (`TelemetrixClient`, `connect`), `models` (frozen dataclasses, `Quality` enum), `errors`, and private `_time`, `_cursor`, `_queries` (SQL composition, pure and unit-tested).

### Connection and lifecycle
`connect(settings=None)` returns a `TelemetrixClient` usable as a context manager. `ConnectionSettings(conninfo="", password=None, connect_timeout=10, query_timeout=30, application_name="telemetrix-query")`; `ConnectionSettings.from_env()` reads `TELEMETRIX_DATABASE_URL` (libpq URI or key/value string), `TELEMETRIX_QUERY_TIMEOUT` (seconds); everything not given falls back to libpq (`PGHOST`, `PGUSER`, `PGPASSWORD`, `~/.pgpass`, …). `repr` never shows a password.

The session is opened with `options=-c default_transaction_read_only=on -c TimeZone=UTC` and autocommit; each query runs in its own transaction with `SET LOCAL statement_timeout`. At connect the client reads the schema (`to_regclass`, `information_schema.columns`) into `Capabilities(rollups, gaps, derived_tags, can_write, server_version, timescaledb_version)`; missing `asset`/`tag`/`measurement` raises `SchemaMismatchError`. `can_write` = `has_table_privilege(current_user,'measurement','INSERT')`; when true, `InsecureRoleWarning` is issued. One client wraps one connection and is not thread-safe, except `cancel()`, which may be called from another thread.

### Queries (ADR-0020)
| Method | Selection | Order | Result |
| --- | --- | --- | --- |
| `plants()` | — | plant | `list[Plant]` |
| `lines(plant_id=)` | plant | plant, line | `list[Line]` |
| `assets(plant_id=, line_id=, asset_type=)` | hierarchy | asset | `list[Asset]` |
| `tags(plant_id=, line_id=, asset_id=, names=, derived=)` | hierarchy, names, raw/derived | asset, name | `list[Tag]` |
| `measurements(start, end, …, qualities=, limit=, cursor=, timeout=)` | hierarchy, `tags` names, `tag_ids`, Quality | time, tag_id | `Page[Measurement]` |
| `iter_measurements(start, end, …, page_size=)` | as above | time, tag_id | `Iterator[Measurement]` |
| `rollups(start, end, …, limit=, cursor=)` / `iter_rollups` | hierarchy, Tags | bucket, tag_id | `Page[Rollup]` / iterator |
| `gaps(start, end, plant_id=, line_id=, asset_id=, limit=, cursor=)` / `iter_gaps` | hierarchy | gap start, event id | `Page[Gap]` / iterator |

Every hierarchy filter is an exact match and filters combine with AND. `Measurement` holds `time, tag_id, plant_id, line_id, asset_id, tag, unit, value, quality, batch_id, ingested_at`; `Rollup` holds `bucket, tag_id, plant_id, line_id, asset_id, tag, unit, min, max, avg, count`; `Gap` holds `event_id, plant_id, line_id, asset_id, endpoint, start, end, reason, occurred_at`; `Tag` holds ids, hierarchy, `name, unit, min_valid, max_valid, derived_by`.

Intervals: `start` and `end` are aware datetimes or ISO-8601 strings with an offset, converted to UTC; `end > start`; Measurements `start <= time < end`; Rollups `start <= bucket < end`; Gaps `gap_start < end AND gap_end > start` (only events with both bounds). Pages fetch `limit + 1` rows to decide whether a next page exists. The cursor is URL-safe base64 of `{"v":1,"k":[…last key…],"q":<fingerprint>}`, where the fingerprint is a SHA-256 of the query kind and normalised filters; a mismatch raises `InvalidCursorError`. `limit` / `page_size` are 1–100,000.

### Errors
`TelemetrixQueryError` is the base. `ConnectionFailedError` (connect or lost connection; message names host/port, never the password), `QueryTimeoutError` (statement timeout), `QueryCancelledError` (`cancel()`), `PermissionDeniedError` (names the role to grant), `SchemaMismatchError`, `FeatureUnavailableError` (names the migration file), `InvalidCursorError` (also a `ValueError`), `ClientClosedError`. Bad arguments raise `ValueError` / `TypeError`. Ctrl-C in a notebook cancels the running statement through psycopg and raises `KeyboardInterrupt`.

### Reader role (ADR-0021)
```sql
DO $$ BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'telemetrix_reader') THEN
    CREATE ROLE telemetrix_reader NOLOGIN;
  END IF;
END $$;
DO $$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO telemetrix_reader', current_database()); END $$;
GRANT USAGE ON SCHEMA public TO telemetrix_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO telemetrix_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO telemetrix_reader;
```
`ALL TABLES` covers views, so `measurement_1m` is included; the continuous aggregate reads its materialisation with the owner's rights.

### Verification
Pure unit tests cover settings, interval parsing, cursor binding and SQL composition. Integration tests start `timescale/timescaledb:2.30.2-pg17` with testcontainers, apply `init.sql` and every migration with psql inside the container, seed known Measurements, Rollups and Gaps, and connect as a reader login with a generated password. A `.NET` integration test proves the migration is idempotent and grants SELECT only. The CI job builds the wheel, installs it in a clean environment and runs both test suites and the examples.

## Risks / Trade-offs

- [A schema change breaks consumers silently] → capabilities checked at connect, SDK tests run in CI against the real migrations, compatibility documented per release.
- [Large intervals overload the database] → page limit, statement timeout and keyset paging on the `(time, tag_id)` order.
- [Late or replayed Batches are missed by an in-progress iteration] → documented; a new query sees them.
- [Rollups lag the newest minute] → documented; query Measurements for the newest minutes.
- [Notebook users keep the owner credentials] → `InsecureRoleWarning` and documented reader-login setup.

## Migration Plan

No service changes. Review and apply `004_query_reader.sql` with the `migrations` profile, create a reader login interactively, install the package with pip. Rollback: `DROP OWNED BY telemetrix_reader; DROP ROLE telemetrix_reader;` after dropping its member logins; uninstall the package.
