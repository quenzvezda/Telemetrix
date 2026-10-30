# M11 review package: Query SDK

Change: [`add-query-sdk`](../openspec/changes/add-query-sdk/) · Issue: [#8](https://github.com/quenzvezda/Telemetrix/issues/8) · Branch: `m11-add-query-sdk`

On 2026-10-31 the reviewer asked for issue #8 to be implemented to completion, using the agent's recommendation for each open decision. This package lists those decisions first, because they are the part most worth challenging, then the code to read, the automated evidence and the manual checks for the issue's acceptance criteria. Final acceptance and archival remain the reviewer's.

## 1. Decisions to review

| Issue question | Recommendation taken | ADR | Main alternative rejected |
| --- | --- | --- | --- |
| Language scope, package names, distribution | Python only: distribution `telemetrix-query`, import `telemetrix_query`, in `sdk/python/`, installed locally with pip or as a wheel. **.NET deferred**, not rejected. | [0019](adr/0019-ship-a-python-query-sdk-first.md) | .NET first (no requested consumer is .NET; Prefect needs Python); both at once |
| Stable API vs SQL escape hatch | Typed methods only; every statement fixed in the SDK and parameterised. Ad-hoc SQL stays possible with psycopg and the reader role, outside the API. | [0020](adr/0020-stable-query-api-with-half-open-utc-intervals-and-keyset-pages.md) | A raw-SQL method (callers would depend on table layout again) |
| Time boundaries | Aware datetimes or ISO strings with offset only; half-open `start <= time < end` on the source timestamp; Rollups by bucket start; Gaps by overlap; UTC out. | 0020 | Assuming UTC for naive values (silently shifts data) |
| Ordering, pagination consistency, limits | Order `(time, tag_id)`; keyset cursors bound to their query; 10,000 per page by default, 100,000 maximum; iterators stream one page per short transaction. | 0020 | Offset paging (pages shift under inserts); server-side cursors (hold a snapshot open in notebooks) |
| Timeouts, cancellation, errors | Statement timeout per query (30 s default, per call override), `cancel()` from another thread, Ctrl-C cancels server-side; one exception type per failure, no credentials in messages. | 0020 | — |
| Read-only role, migration | Migration `004_query_reader.sql`: `NOLOGIN` role `telemetrix_reader`, SELECT on all tables/views plus default privileges. Logins created by the operator with an interactive password. SDK sessions read-only regardless of login; warning when the login can write. | [0021](adr/0021-read-only-reader-role-and-external-credentials.md) | Password in a migration (secret in git); owner login (one leaked notebook could drop data) |
| Schema compatibility, retained data | Core tables checked at connect; Rollups (001), Gaps (002), derived Tags (003) detected; missing migration → error naming the file. SemVer 0.x; supported schema documented per release. Expired data → empty results. | 0019 | Failing on any missing migration (initial-schema databases could not be read) |

No service, Kafka contract or existing table changes. The SDK does not need inline scripting or Prefect.

## 2. Commits and what to read

| Commit | Read |
| --- | --- |
| `docs(adr): record query sdk decisions for m11` | ADR-0019–0021, `openspec/changes/add-query-sdk/design.md`, `specs/query-sdk/spec.md` |
| `feat(db): add a select-only reader role for query consumers` | `deploy/sql/migrations/004_query_reader.sql`, `QueryReaderMigrationTests.cs` |
| `feat(sdk): add the read-only python query client` | `sdk/python/src/telemetrix_query/client.py` (`connect`, `_run`, `_translate`), `_queries.py` (all SQL), `_time.py`, `_cursor.py` |
| `test(sdk): verify queries against a real timescaledb` | `sdk/python/tests/integration/conftest.py` (seeded data), `test_queries.py`, `test_access.py` |
| `docs(sdk): document the query sdk and add runnable examples` | `sdk/python/README.md` (*Query semantics*, *Compatibility*), `examples/` |
| `ci: build, install and test the python query sdk` | `.github/workflows/ci.yml` job `python-sdk` |

## 3. Automated evidence

| Check | Result |
| --- | --- |
| `npx -y @fission-ai/openspec@1.14.3 validate add-query-sdk --strict` | valid |
| `dotnet test tests/Telemetrix.Integration.Tests --filter "FullyQualifiedName~QueryReaderMigration\|…ShellRunner"` | 3 passed: 004 applies twice through `migrate.sh`; the reader selects all seven telemetry relations and a table created later; INSERT, UPDATE, DELETE, TRUNCATE and CREATE fail with `42501` |
| SDK unit tests (`sdk/python/tests/unit`) | 40 passed: environment settings and password redaction, aware/naive/offset intervals, cursor round-trip and mismatch, limits, filter composition, SQL injection attempt stays a parameter |
| SDK integration tests (`sdk/python/tests/integration`), wheel installed into a clean venv | 29 passed (69 in total): Tag metadata, half-open boundaries, offsets, Quality filters, null/Bad/empty, 25,000 rows streamed in 1,000-row pages, pages under concurrent inserts, cursor reuse, Rollups (Good only), Gaps (overlap), initial-schema database, timeout, cancellation, missing privilege, unreachable host and wrong password without leaking it, owner warning and read-only session, both examples |
| Full Release suite (`dotnet build -c Release`, `dotnet test -c Release`) | Build: 0 warnings, 0 errors. First full run: 332 passed, 1 skipped (explicit load profile), 1 Integration.Tests failure whose test name was lost to truncated output (Kafka coordinator-load warnings in the log); an immediate rerun of Integration.Tests passed all 65 with the same skip. Treated as an existing timing flake; CI on the PR is the tie-breaker |
| PR CI | recorded on the PR |

## 4. Manual checks

Run from the repository root in PowerShell, with the stack running and live data flowing (`docker compose -f deploy/docker-compose.yml --profile pipeline up -d`). Each step names the acceptance criterion it covers.

### 4.1 Read-only role and login (acceptance 4)

Review `deploy/sql/migrations/004_query_reader.sql`, then apply the migrations (001–003 are idempotent and change nothing when already applied):

```powershell
docker compose -f deploy/docker-compose.yml --profile migrations run --rm db-migrate
```

Expected: the output ends with `Applying 004_query_reader.sql` and no error. Create a login and type its password when prompted:

```powershell
docker compose -f deploy/docker-compose.yml exec timescaledb psql -U telemetry -d telemetry -c "CREATE ROLE analyst LOGIN IN ROLE telemetrix_reader"
docker compose -f deploy/docker-compose.yml exec -it timescaledb psql -U telemetry -d telemetry -c "\password analyst"
```

Prove it cannot write:

```powershell
docker compose -f deploy/docker-compose.yml exec timescaledb psql -U analyst -d telemetry -c "DELETE FROM measurement"
```

Expected: `ERROR:  permission denied for table measurement`. `SELECT count(*) FROM measurement` as `analyst` succeeds.

### 4.2 A separate consumer installs the package (acceptance 1)

```powershell
python -m venv $env:TEMP\telemetrix-consumer
& $env:TEMP\telemetrix-consumer\Scripts\pip install ./sdk/python
$env:TELEMETRIX_DATABASE_URL = "postgresql://analyst@localhost:5432/telemetry"
$env:PGPASSWORD = Read-Host -MaskInput "analyst password"
```

Expected: pip installs `telemetrix-query 0.1.0` and `psycopg`; nothing from `src/Telemetrix.*` is involved.

### 4.3 Tag metadata, Asset Measurements, Quality filter and Rollups (acceptance 2)

```powershell
& $env:TEMP\telemetrix-consumer\Scripts\python sdk/python/examples/asset_health_report.py --asset Pump01 --hours 1
```

Expected: one line per Pump01 Tag with `measurements=… good=… uncertain=… bad=… null=…`, one `rollups` line per Tag with min/max/avg and its unit, and a `gaps=` line. Then try the API directly:

```powershell
& $env:TEMP\telemetrix-consumer\Scripts\python -c "from datetime import datetime, timedelta, timezone as tz; from telemetrix_query import connect, Quality; c = connect(); e = datetime.now(tz.utc); s = e - timedelta(minutes=10); print([(t.name, t.unit, t.min_valid, t.max_valid) for t in c.tags(asset_id='Tank01')]); p = c.measurements(s, e, asset_id='Tank01', tags='Temperature', limit=3); print([(m.time.isoformat(), m.value, m.quality.name) for m in p], p.has_more); print(len(list(c.iter_measurements(s, e, asset_id='Pump01', qualities=Quality.BAD))), 'bad'); print(list(c.iter_rollups(s, e, asset_id='Tank01', tags='Level'))[:2])"
```

Expected: Tank01 Tags with units and ranges; three Temperature Measurements with UTC `+00:00` times and `True` (more pages); a count of Bad Pump01 Measurements (0 unless a fault was injected — inject one from PlantSim to see it rise); up to two Level Rollups. The notebook `sdk/python/examples/query_tour.ipynb` shows the same steps; it runs in Jupyter after `pip install notebook` in the consumer venv.

### 4.4 Boundaries, empty results and large results (acceptance 3)

Compare the SDK with SQL over the same half-open interval:

```powershell
$end = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:00Z"); $start = (Get-Date).ToUniversalTime().AddMinutes(-30).ToString("yyyy-MM-ddTHH:mm:00Z")
docker compose -f deploy/docker-compose.yml exec timescaledb psql -U analyst -d telemetry -tAc "SELECT count(*) FROM measurement m JOIN tag t USING (tag_id) WHERE t.asset_id='Pump01' AND m.time >= '$start' AND m.time < '$end'"
& $env:TEMP\telemetrix-consumer\Scripts\python -c "import sys; from telemetrix_query import connect; print(sum(1 for _ in connect().iter_measurements(sys.argv[1], sys.argv[2], asset_id='Pump01', page_size=1000)))" $start $end
```

Expected: both numbers are equal (about 9,000 for five Tags at 1 Hz), streamed in 1,000-row pages. An interval in the past with no data, e.g. `2020-01-01T00:00:00Z` to `2020-01-02T00:00:00Z`, prints `0`; a naive time such as `2026-10-31T10:00:00` raises `ValueError: start has no time zone`.

### 4.5 Errors and secrets (acceptances 3 and 4)

1. Stop the database (`docker compose -f deploy/docker-compose.yml stop timescaledb`) and run the report again. Expected: `ConnectionFailedError: cannot connect to host=localhost port=5432 dbname=telemetry user=analyst: …` without the password. Start it again.
2. Connect as the owner: set `TELEMETRIX_DATABASE_URL` to `postgresql://telemetry@localhost:5432/telemetry` with its password. Expected: `InsecureRoleWarning: login 'telemetry' can write telemetry …`; queries still work read-only.
3. `git grep -n -i "password" -- sdk deploy/sql/migrations/004_query_reader.sql` shows only documentation and generated test passwords, no secret.

### 4.6 Language scope (acceptance 5)

Confirm the recorded scope: Python ships, .NET is deferred ([ADR-0019](adr/0019-ship-a-python-query-sdk-first.md), `sdk/python/README.md`, ROADMAP M11). If you want .NET in this milestone instead, say so before acceptance.

## 5. Known limitations

- No .NET package and no PyPI publication; consumers install from the repository or a wheel.
- Grafana and the services still connect as the `telemetry` owner; moving Grafana to the reader role is possible later.
- Capabilities are detected at connect: after applying a migration, reconnect.
- Rollups appear after the continuous-aggregate refresh (about one to two minutes); Measurements stored later with a time before an iteration's cursor are seen only by a new query.
- One connection per client, synchronous only; no pooling or async API. pandas is optional, not a dependency.
- Transformation lineage (issue #13) and dead-letter inspection (issue #11) are not part of this API.
