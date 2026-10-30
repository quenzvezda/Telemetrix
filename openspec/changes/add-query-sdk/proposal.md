# Proposal

## Why

Notebooks, applications and scheduled jobs that analyse stored telemetry each have to know the table layout, join `measurement` to `tag` and `asset`, get UTC boundaries right and remember that Rollups hold Good Measurements only ([issue #8](https://github.com/quenzvezda/Telemetrix/issues/8)). The only C# database access is internal to `Telemetrix.Transformer/Storage/`, and the services connect as the database owner. There is no supported, read-only way to query telemetry.

Milestone: M11 — Query SDK (post-M10 backlog). On 2026-10-31 the reviewer asked for issue #8 to be implemented to completion, using the agent's recommendation for each open decision; the decisions are recorded in ADR-0019, ADR-0020 and ADR-0021 for review.

## What Changes

- FR-50: a Python package `telemetrix-query`, installed locally with pip, queries Plants, Lines, Assets, Tags, Measurements, one-minute Rollups and collection Gaps without SQL or Transformer code; .NET is deferred (ADR-0019).
- FR-51: queries take half-open, timezone-aware intervals on the source timestamp, return typed UTC results with unit, Quality, Batch identity and ingest time, and are bounded by keyset pages or streamed page by page; timeouts, cancellation and errors are explicit (ADR-0020).
- FR-52: migration `004_query_reader.sql` adds the `telemetrix_reader` group role with SELECT only; SDK sessions are read-only and credentials come from the environment or explicit settings (ADR-0021, NFR-10).
- NFR-13: unit tests and integration tests against a real TimescaleDB, run in a new CI job.
- Documentation: installation, configuration, query semantics, compatibility and versioning policy, a notebook and a post-storage analysis example.

## Capabilities

### New Capabilities
- `query-sdk`: supported read-only access to stored telemetry for external consumers — package, query semantics, errors, reader role and compatibility.

### Modified Capabilities
None.

## Impact

- New `sdk/python/` project (`telemetrix_query`), depending on psycopg 3; tests use pytest and testcontainers.
- Database: migration `deploy/sql/migrations/004_query_reader.sql`, applied explicitly; no change to existing tables or services.
- CI: a `python-sdk` job builds the wheel, installs it and runs the test suite with Docker.
- Docs: ADR-0019–0021, CONTEXT.md term, FR-50–FR-52 in the design document, SDK README, review package.
