# Tasks

On 2026-10-31 the reviewer asked for issue #8 to be implemented to completion, accepting the agent's recommendation for each open decision (ADR-0019–0021) and reviewing the result as a PR. Final manual acceptance and archival remain the reviewer's.

## 1. Decisions

- [x] 1.1 Record ADR-0019 (Python first, .NET deferred), ADR-0020 (stable API, half-open UTC intervals, keyset pages) and ADR-0021 (reader role, external credentials), the M11 roadmap row, the CONTEXT.md term and FR-50–FR-52 in the design document; verify `npx -y @fission-ai/openspec@1.14.3 validate add-query-sdk --strict` passes

## 2. Read-only database access

- [x] 2.1 Add migration `004_query_reader.sql` (reader role, SELECT grants, default privileges); verify `dotnet test tests/Telemetrix.Integration.Tests --filter "FullyQualifiedName~QueryReaderMigration|FullyQualifiedName~TransformationStorageTests.ShellRunner"` shows the migration applies twice, the reader can SELECT every table and a later table, and cannot INSERT, UPDATE, DELETE, TRUNCATE or CREATE

## 3. Python package

- [x] 3.1 Scaffold `sdk/python` (pyproject, models, errors, settings, interval parsing, cursor binding, SQL composition); verify `python -m pytest sdk/python/tests/unit` covers settings from the environment and redaction, aware/naive/offset intervals, cursor round-trip and mismatch, limits and composed filters
- [x] 3.2 Add `TelemetrixClient`: read-only session, capabilities, catalog, Measurement, Rollup and Gap queries with pages and iterators, timeouts, cancellation and error mapping; verify `python -m pytest sdk/python/tests/unit` still passes and `python -c "import telemetrix_query"` works from an installed wheel

## 4. Verification against TimescaleDB

- [x] 4.1 Add the integration suite (container with `init.sql` and all migrations, reader login, seeded data); verify `python -m pytest sdk/python/tests/integration` covers Tag metadata, Asset/interval Measurements, boundaries, offsets, Quality filters, null/Bad/empty, 25,000-row streaming, concurrent inserts, cursor mismatch, Rollups, Gaps, missing migrations, timeout, cancellation, missing privilege, unreachable host, owner warning and read-only session
- [ ] 4.2 Add the `python-sdk` CI job (build the wheel, install into a clean environment, run unit and integration tests); verify the job passes on the PR

## 5. Documentation and milestone verification

- [x] 5.1 Write the SDK README (installation, reader login, configuration, query semantics, errors, compatibility and versioning, deferred .NET) and add a notebook and a post-storage analysis example; verify `python -m pytest sdk/python/tests/integration -k examples` runs both examples against the seeded database
- [x] 5.2 Verify the M11 "Done when" criterion: a separate consumer installs the wheel and queries Tag metadata, Asset Measurements with a Quality filter and Rollups as a reader login; record the review package in `docs/m11-review.md`, run the full Release .NET suite with zero warnings and open the PR
