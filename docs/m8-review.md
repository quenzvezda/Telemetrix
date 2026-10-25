# M8 review package

Change: `add-observability`. Implementation is in progress; OpenSpec remains open.
The reviewer authorized all task groups on 2026-10-25. Final D5 selection and
milestone acceptance remain reviewer checkpoints after measurements are available.

The reviewer requested a PR before archival. The agent may implement all task
groups without intermediate approval; the final PR collects the review checks.

## HTTP hosting

Each service keeps its hosted pipeline and serves HTTP on `Http:Port`:
Bridge 5101, Transformer 5102, PlantSim 5103, MqttSim 5104. Override with a
command-line argument such as `--Http:Port=5201` or environment variable
`Http__Port=5201`. Listeners bind all interfaces; container mappings must match.

Run a configured service, then request its root URL, for example
`Invoke-WebRequest http://localhost:5101/`. The expected status is HTTP 404.
Hosting integration tests additionally store a real MQTT sample through Bridge
and Transformer, receive all seven MQTT topics twice, and read the seven OPC UA
tags while the simulator HTTP listeners are running:

```powershell
dotnet run --project tests/Telemetrix.Integration.Tests -- -class Telemetrix.Integration.Tests.WebHostTests -class Telemetrix.Integration.Tests.SimulatorWebHostTests
```

Both tests passed locally. Docker-based checks use disposable Testcontainers
infrastructure and do not modify the existing Compose database.

## Bridge and Transformer health

Request `http://localhost:5101/health` or `http://localhost:5102/health`.
The JSON names each check and its status. With dependencies available the result
is 200/Healthy. A non-empty Bridge buffer with dependencies up is 200/Degraded.
An unavailable source, Kafka or database reports 503/Unhealthy naming the check.
The HTTP hosting test covers these transitions using disposable dependencies.

## Correlated JSON logging

All four application hosts use JSON console logs with UTC timestamps and scopes.
Bridge batch admission and replay, Transformer consumption/commit and handling
carry `batchId` and `assetId` scopes. Nested storage logs inherit them.
`CorrelatedLoggingTests` checks a nested writer after an asynchronous boundary
using both a scope-capturing provider and the actual host's serialized JSON.
The five store-and-forward unit tests also pass with replay correlation enabled.

Live host verification on 2026-10-25 found the same `batchId`
`01a17160-6f93-7cf6-8511-83818e6f71e5` in parsed JSON logs:
Bridge accepted at 04:45:07.920 UTC, Transformer consumed at 04:45:08.202,
stored at 04:45:08.280 and committed at 04:45:08.301. Each entry's scope
contains that `batchId` and `assetId=Pump01`. Reproduce with the HTTP hosting
test and filter its logs by the new batch identifier (`rg batchId`); the
JSON `Category` distinguishes Bridge, consumption and storage entries.

## Simulator health

`http://localhost:5103/health` reflects PlantSim's active OPC UA server and
simulation loop. `http://localhost:5104/health` reflects MqttSim's broker and
publishing loop. Both are Unhealthy before readiness and after worker shutdown.
The simulator hosting integration test passed with a Mosquitto stop/restart:
MqttSim returned 503 while disconnected, then 200 and fresh publications after
reconnection. All seven OPC UA tags remained readable.

## Pipeline metrics

Bridge's `Telemetrix.Bridge` meter counts accepted samples by asset/source,
counts Kafka-acknowledged raw batches by asset, and observes disk buffer depth.
The depth includes pending plant-event entries sharing the same durable queue.
`BridgeMetricsTests` passes with real batching and gauge values 0, 1, 0 across
buffer append/acknowledgement; no tag or batch identifier is a metric label.

Transformer metrics count committed new measurements, acknowledged dead-letter
batches and write duration in milliseconds. An independent Kafka handle samples
assigned-partition high watermarks minus committed next offsets every 10 seconds;
it never subscribes or calls the raw consumer from another thread.
`TransformerMetricsTests` passed: lag grew to three batches during a blocked
write and fell to zero after release, exactly 30 new measurements were counted,
a replay added none, and an invalid batch incremented dead-letter count once.

Start the optional container pipeline and Prometheus after setting `deploy/.env`:

```powershell
docker compose -f deploy/docker-compose.yml --profile metrics up -d --build
```

At `http://localhost:9090`, query `up`, `telemetrix_samples_received_total`,
`telemetrix_batches_produced_total`, `telemetrix_rows_written_total`,
`telemetrix_buffer_depth` and `telemetrix_consumer_lag`. Scrapes run every five
seconds. Both targets returned `up=1` locally; the raw endpoints at ports 5101
and 5102 exported these series with `service_name` labels. HTTP integration
verification also passed with the Prometheus exporter enabled. Set
`Telemetry__Prometheus__Enabled=false` to disable `/metrics`; development can
use `Telemetry__Console__Enabled=true` for the diagnostic console exporter.

Dependencies are pinned to OpenTelemetry 1.18.0 and its matching Prometheus
ASP.NET Core exporter 1.18.0-beta.1. The API usage follows the
[upstream exporter documentation](https://github.com/open-telemetry/opentelemetry-dotnet/blob/coreunstable-1.18.0-beta.1/src/OpenTelemetry.Exporter.Prometheus.AspNetCore/README.md).

## Rollups and retention SQL

Review `deploy/sql/migrations/001_rollups_and_retention.sql`. It adds a
one-minute Good-only rollup, one-minute refresh schedule, 30-day raw retention
and one-year rollup retention. `RollupMigrationTests` passed on a fresh
TimescaleDB 2.30.2/PostgreSQL 17 Testcontainer: applying the migration twice
succeeded, retained a pre-existing measurement, and created one refresh policy
and exactly two retention policies. Existing local volumes have not been migrated.

For the existing M7 volume, stop Bridge, wait for Transformer lag to reach zero,
then stop Transformer before comparing counts. Record `SELECT count(*) FROM measurement`, review each migration SQL,
and explicitly run:

```powershell
docker compose -f deploy/docker-compose.yml --profile migrations run --rm db-migrate
```

Run the count query again before resuming Bridge; it must match. Confirm
`SELECT * FROM timescaledb_information.continuous_aggregates` includes
`measurement_1m`, then inspect `timescaledb_information.jobs` for its policies.
Repeat the migration command: it must succeed without changing measurements.
The migration runner stops on the first SQL error. Default Compose startup
does not run `db-migrate`. Task 4.2 remains pending the reviewer's existing-volume
verification; automated checks only mutate disposable databases.

The Good-only rollup test passed after refreshing a completed minute containing
10/20/30 Good and 999 Bad: min 10, max 30, average 20, count 3.

The jobs inspection test joined `timescaledb_information.jobs` to
`timescaledb_information.continuous_aggregates` to resolve the rollup's internal
hypertable. It returned exactly `measurement = 30 days` and
`measurement_1m = 1 year`. All three rollup/migration tests passed.

Migration 001 also backfills completed historical buckets from the oldest raw
bucket still present. The refresh policy alone only covers its rolling one-hour
window. The regression test now includes a measurement from two days earlier,
then removes that raw history, inserts newer raw data and reruns the migration;
both the older retained rollup and the new one remain. All three tests pass with
this history coverage. The test driver executes backfill separately from DDL,
matching `psql -f` rather than putting the refresh procedure in an Npgsql pipeline.

## Plant event storage

Review `deploy/sql/migrations/002_plant_events.sql` together with migration 001.
After applying them, enable `PlantEvents__Enabled=true` on Transformer and
restart it. This opt-in keeps an existing M7 database running until SQL review.
The consumer group `transformer-events` stores events with `event_id` uniqueness
and commits Kafka offsets only after the insert succeeds. Bridge still creates
`plant.events`. The storage integration test passed: ConnectionLost,
ConnectionRestored and a 60-second Gap were stored, and replaying all three
left exactly three stored events after all six Kafka deliveries were committed.

## Grafana dashboard

After migration review/application, set `PLANT_EVENTS_ENABLED=true` in
`deploy/.env` and restart the container pipeline:

```powershell
docker compose -f deploy/docker-compose.yml --profile metrics up -d --build
```

Open `http://localhost:3000/d/telemetrix`. Select `Pump01` and all tags. Expect
individual tag trends with units, a Running/Stopped state timeline, current
state with last-change/sample timestamps, and a gap table with UTC bounds.
Refresh is ten seconds. A seven-day range uses one-minute rollups; ranges
below six hours use raw Good measurements. Lines contain explicit nulls at
source gaps. Select `Tank01` to see its two tags.

For an outage check, stop PlantSim for at least 60 seconds, start it again,
then wait for Bridge recovery and its next batch:

```powershell
docker compose -f deploy/docker-compose.yml stop plantsim
Start-Sleep -Seconds 60
docker compose -f deploy/docker-compose.yml up -d plantsim
```

The gap table must show both bounds, the trends must break over the gap, and
the state timeline must resume after recovery. `DashboardTests` passed on a
fresh Grafana/Testcontainers pipeline: the automated outage produced an 80-second
source gap including reconnection; raw trend responses contained null markers
and the seven-day rollup query returned data. Visual review of a separate run
confirmed repeated tag panels, pump state and a 65.0-second gap in the table;
the dashboard's export view recognized `telemetrix` as the dashboard resource.
The first visual run exposed a Windows SQLite cleanup ordering issue in the
test harness; hosts are now disposed before their temporary buffer is removed,
and the complete subsequent run passed.

To verify pump state manually, use UaExpert at `opc.tcp://localhost:4840`,
browse `Objects/Plant01/Line01/Simulation`, and call `TripPump`. On the
`Pump01` dashboard, the next refresh must show Stopped and its change time;
call `StartPump` to recover. The final automated fresh-Grafana run reported
Stopped through the actual panel query in 5.887 seconds, within one 10-second
refresh interval. Its timeline data and the visual review included the state
transition and recovery.

## Load tooling

See `docs/load-test.md` for hardware, commands and exact profile settings.
`deploy/Test-M8Load.ps1` runs disposable infrastructure and writes a JSON report.
Clone identity/completed-tick tests and the existing MqttSim health/reconnection
test both pass. A short 1,024-batch-queue profile reached 5,014.484 received
samples/s and drained all 152,152 publications with zero dead-letter batches.
The bounded queue regression failed before using its configured capacity and
passed afterward. Final duration measurements and D5 selection are tracked
separately; a smoke run never marks the long-run acceptance tasks complete.
