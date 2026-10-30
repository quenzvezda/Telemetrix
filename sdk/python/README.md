# telemetrix-query

Read-only Python SDK for telemetry that Telemetrix stored in TimescaleDB. Notebooks, applications and scheduled jobs query the plant hierarchy, Tags, Measurements, one-minute Rollups and collection Gaps through one typed API instead of copying SQL and schema knowledge ([issue #8](https://github.com/quenzvezda/Telemetrix/issues/8)).

- **Language scope:** Python only. A .NET package is deferred, not rejected ([ADR-0019](../../docs/adr/0019-ship-a-python-query-sdk-first.md)).
- **Access:** direct, read-only database access; no Transformer code, no inline scripting and no Prefect required.
- **Requirements:** Python 3.11+, network access to the TimescaleDB port, a login in the `telemetrix_reader` role.

## Install

The package is installed locally from this repository; publication to PyPI is a separate step.

```bash
pip install ./sdk/python
```

To hand a consumer a single file, build a wheel and install that:

```bash
python -m pip install build
python -m build --wheel --outdir dist sdk/python
pip install dist/telemetrix_query-0.1.0-py3-none-any.whl
```

The only runtime dependency is `psycopg[binary]` 3.2+.

## Database access

Consumers use a login that can only read ([ADR-0021](../../docs/adr/0021-read-only-reader-role-and-external-credentials.md)).

1. Review and apply the migrations, including `004_query_reader.sql`, which creates the `NOLOGIN` role `telemetrix_reader` with SELECT on every telemetry table and view:

   ```bash
   docker compose -f deploy/docker-compose.yml --profile migrations run --rm db-migrate
   ```

2. Create a login in that role and type its password interactively, so it never lands in a file or the shell history:

   ```bash
   docker compose -f deploy/docker-compose.yml exec timescaledb psql -U telemetry -d telemetry -c "CREATE ROLE analyst LOGIN IN ROLE telemetrix_reader"
   ```

   ```bash
   docker compose -f deploy/docker-compose.yml exec -it timescaledb psql -U telemetry -d telemetry -c "\password analyst"
   ```

Whatever the login, every SDK session is read-only (`default_transaction_read_only`). Connecting as a login that can write Measurements, such as the `telemetry` owner, works but issues an `InsecureRoleWarning`.

## Configure

`connect()` with no argument reads the environment:

| Variable | Meaning |
| --- | --- |
| `TELEMETRIX_DATABASE_URL` | libpq URI or key/value string, e.g. `postgresql://analyst@localhost:5432/telemetry` |
| `TELEMETRIX_QUERY_TIMEOUT` | default statement timeout in seconds (30) |
| `PGPASSWORD`, `PGPASSFILE`, `PGHOST`, `PGUSER`, … | standard libpq variables for anything the URL leaves out |

Keep the password out of the URL: use `PGPASSWORD` in the environment, or a [password file](https://www.postgresql.org/docs/17/libpq-pgpass.html) (`~/.pgpass`, `%APPDATA%\postgresql\pgpass.conf` on Windows).

```bash
export TELEMETRIX_DATABASE_URL=postgresql://analyst@localhost:5432/telemetry
read -rsp "Password: " PGPASSWORD && export PGPASSWORD
```

```powershell
$env:TELEMETRIX_DATABASE_URL = "postgresql://analyst@localhost:5432/telemetry"
$env:PGPASSWORD = Read-Host -MaskInput "Password"
```

Applications can pass settings explicitly, for example with the password from their own secret store:

```python
from telemetrix_query import ConnectionSettings, connect

settings = ConnectionSettings(conninfo="host=db.internal dbname=telemetry user=analyst",
                              password=secret_store.get("telemetrix-analyst"), query_timeout=60)
client = connect(settings)
```

`repr(settings)` and every error message omit the password.

## Quick start

```python
from datetime import datetime, timedelta, timezone
from telemetrix_query import Quality, connect

end = datetime.now(timezone.utc)
start = end - timedelta(hours=1)

with connect() as client:
    for tag in client.tags(asset_id="Pump01"):
        print(tag.name, tag.unit, tag.min_valid, tag.max_valid)

    page = client.measurements(start, end, asset_id="Pump01", tags="MotorTemperature", limit=1_000)
    for m in page:
        print(m.time, m.value, m.quality.name, m.batch_id)

    bad = list(client.iter_measurements(start, end, asset_id="Pump01", qualities=Quality.BAD))
    rollups = list(client.iter_rollups(start, end, asset_id="Pump01", tags="FlowRate"))
    gaps = list(client.iter_gaps(start, end, asset_id="Pump01"))
```

## API

| Method | Returns | Filters |
| --- | --- | --- |
| `plants()` | `list[Plant]` | — |
| `lines(plant_id=)` | `list[Line]` | Plant |
| `assets(plant_id=, line_id=, asset_type=)` | `list[Asset]` | hierarchy, Asset type |
| `tags(plant_id=, line_id=, asset_id=, names=, derived=)` | `list[Tag]` with unit, valid range, `derived_by` | hierarchy, Tag names, `derived=True/False` |
| `measurements(start, end, …, limit=, cursor=)` | `Page[Measurement]` | hierarchy, `tags` (names), `tag_ids`, `qualities` |
| `iter_measurements(start, end, …, page_size=)` | iterator of `Measurement` | as above |
| `rollups(start, end, …, limit=, cursor=)` / `iter_rollups` | `Page[Rollup]` / iterator | hierarchy, `tags`, `tag_ids` |
| `gaps(start, end, …, limit=, cursor=)` / `iter_gaps` | `Page[Gap]` / iterator | hierarchy |
| `capabilities` | which optional tables exist, whether the login can write | — |
| `cancel()`, `close()` | | |

Filters are exact matches and combine with AND; `tags`, `tag_ids` and `qualities` take one value or a collection. Every query method also takes `timeout=` (seconds). A `Measurement` carries `time`, `tag_id`, `plant_id`, `line_id`, `asset_id`, `tag`, `unit`, `value`, `quality`, `batch_id` and `ingested_at`; a `Rollup` carries `bucket`, the same Tag identity, `min_value`, `max_value`, `avg_value` and `sample_count`; a `Gap` carries `event_id`, the hierarchy, `endpoint`, `start`, `end`, `reason` and `occurred_at`.

## Query semantics

These rules are the stable contract of the API ([ADR-0020](../../docs/adr/0020-stable-query-api-with-half-open-utc-intervals-and-keyset-pages.md)).

**Time.** `start` and `end` are timezone-aware `datetime`s or ISO-8601 strings with an offset (`2026-10-01T00:00:00Z`, `2026-10-01T07:00:00+07:00`). Naive values raise `ValueError` instead of guessing a zone, and `end` must be after `start`. The interval is half-open, `start <= time < end`, on the source timestamp, so consecutive intervals never return a Measurement twice. Rollups are selected by bucket start (`start <= bucket < end`); a Gap is returned when it overlaps the interval (`gap.start < end` and `gap.end > start`). Every returned time is UTC.

**Values.** An interval without Measurements returns an empty page; it is not an error. A stored null value is `None`, whatever its Quality. Bad Measurements are returned unless you filter them out, and a Bad Measurement may still carry the value that made it Bad (outside the Tag's valid range). Derived Tags from Python transformations appear like other Tags, with `derived_by` naming the transformation label.

**Order and pages.** Measurements and Rollups are ordered by time, then Tag id; Gaps by start time, then event id. A page holds at most `limit` items (default 10,000, maximum 100,000). `page.next_cursor` is `None` on the last page; otherwise pass it back with the same interval and filters to read the next page. Cursors are keyset positions: pages never repeat or skip rows that existed when the first page was read, even while new Measurements arrive. A cursor used with a different query raises `InvalidCursorError`. A Measurement stored later with a time before the cursor (a late or replayed Batch) appears only in a new query.

**Large results.** `iter_measurements`, `iter_rollups` and `iter_gaps` read one page at a time, so memory stays bounded by `page_size` whatever the interval. Each page is its own short read-only transaction; no snapshot is held open while you iterate.

**Rollups.** Rollups summarise Good Measurements only. They come from the `measurement_1m` continuous aggregate, which refreshes every minute with a one-minute end offset, so the newest minutes appear after a short delay; query Measurements for them.

**Retention.** Raw Measurements older than 30 days and Rollups older than one year are dropped by the database (migration 001). Queries over expired data return empty pages.

## Errors, timeouts and cancellation

| Exception | When |
| --- | --- |
| `ConnectionFailedError` | the database cannot be reached, authentication fails, or the connection was lost; create a new client |
| `QueryTimeoutError` | the statement ran longer than its timeout (client default or `timeout=`); the client stays usable |
| `QueryCancelledError` | `client.cancel()` was called from another thread |
| `PermissionDeniedError` | the login is not in `telemetrix_reader`; the message says what to grant |
| `SchemaMismatchError` | the database has no `asset` / `tag` / `measurement` table, or the schema changed incompatibly |
| `FeatureUnavailableError` | Rollups or Gaps were requested but migration 001 or 002 is not applied; the message names the file |
| `InvalidCursorError` | the cursor is malformed or belongs to another query (also a `ValueError`) |
| `ClientClosedError` | the client was closed |

All derive from `TelemetrixQueryError`. Invalid arguments raise `ValueError` or `TypeError` before any query runs. Interrupting a notebook cell (Ctrl-C) cancels the running statement on the server and raises `KeyboardInterrupt`.

A client wraps one database connection. Use it as a context manager, or call `close()`. It is not thread-safe, except `cancel()`; give each thread its own client.

## Compatibility and versioning

| telemetrix-query | Database schema | Python |
| --- | --- | --- |
| 0.1.x | `deploy/sql/init.sql`; optional migrations 001 (Rollups), 002 (Gaps), 003 (derived Tags), 004 (reader role) | 3.11+ |

At connect the client checks that `asset`, `tag` and `measurement` exist and detects the optional tables (`client.capabilities`), so a missing migration produces an error naming the file rather than a SQL error. The package follows SemVer with a 0.x major: a patch release never changes the API, a minor release may. A schema change that renames or removes something the SDK reads ships with a new SDK release in the same change, and this table records it.

## Examples

- [`examples/query_tour.ipynb`](examples/query_tour.ipynb) — notebook: Tag metadata, an Asset's Measurements, Quality filters, Rollups, optional pandas.
- [`examples/asset_health_report.py`](examples/asset_health_report.py) — post-storage analysis: Quality and null counts per Tag, Rollup summary and Gap time for an Asset.

```bash
python sdk/python/examples/asset_health_report.py --asset Pump01 --hours 1
```

## Development

```bash
python -m venv sdk/python/.venv
sdk/python/.venv/bin/pip install -e "sdk/python[test]"
sdk/python/.venv/bin/python -m pytest sdk/python/tests/unit
sdk/python/.venv/bin/python -m pytest sdk/python/tests/integration
```

On Windows use `.venv\Scripts\python`. Integration tests need Docker: they start `timescale/timescaledb:2.30.2-pg17`, apply `init.sql` and every migration with the shipped runner, create logins with generated passwords and seed known data. CI builds the wheel and runs both suites from a clean environment on Python 3.11 and 3.13.
