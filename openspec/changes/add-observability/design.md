# Design

## Context

Bridge and transformer are Worker Services with default console logging and no HTTP surface. `deploy/sql/init.sql` creates the schema but runs only when the `tsdata` volume is empty, so anything added now must also reach databases created in M1–M7. Grafana is already provisioned with the TimescaleDB data source (`uid: timescaledb`) and a file dashboard provider reading `deploy/grafana/dashboards/`.

## Goals / Non-Goals

**Goals:**
- One metrics vocabulary shared by all services, cheap enough to leave on under load.
- Migrations that work on existing volumes.
- A repeatable load test that produces the NFR-06 / NFR-07 numbers.

**Non-Goals:**
- Distributed tracing across Kafka (batchId log correlation is enough for v1).
- Alerting rules in Grafana (stream alarms are a stretch goal).
- A production metrics backend; Prometheus is optional and local only.

## Decisions

### Health checks
Switch bridge and transformer from `Host.CreateApplicationBuilder` to `WebApplication.CreateSlimBuilder` so they can serve `/health` (and `/metrics` when enabled) on a configurable port, keeping the hosted workers unchanged. Checks: bridge — source session connected, Kafka metadata reachable; transformer — Kafka metadata reachable, `SELECT 1` on TimescaleDB. Buffer non-empty reports Degraded, not Unhealthy, so a draining bridge is not restarted by an orchestrator.
Alternative: a TCP liveness file — rejected; FR-41 asks for HTTP `/health`.

### Metrics
One `Meter` per service: `Telemetrix.Bridge`, `Telemetrix.Transformer`. Instruments (OTel semantic naming, unit in the name suffix):

| Instrument | Type | Service | Labels |
| --- | --- | --- | --- |
| `telemetrix.samples.received` | Counter | bridge | `asset`, `source` |
| `telemetrix.batches.produced` | Counter | bridge | `asset` |
| `telemetrix.buffer.depth` | ObservableGauge (batches) | bridge | — |
| `telemetrix.consumer.lag` | ObservableGauge (messages) | transformer | `partition` |
| `telemetrix.rows.written` | Counter | transformer | — |
| `telemetrix.batches.deadlettered` | Counter | transformer | `reason` |
| `telemetrix.batch.write.duration` | Histogram (ms) | transformer | — |

Consumer lag is computed from `Consumer.Assignment` × `QueryWatermarkOffsets` − committed position, sampled every 10 s. Exporters: console in development, Prometheus scrape endpoint (`OpenTelemetry.Exporter.Prometheus.AspNetCore`) when `Telemetry:Prometheus:Enabled=true`; an optional `prometheus` service is added to compose under a `metrics` profile so the default `docker compose up` stays lean.

### Logging
`AddJsonConsole` with scopes enabled. Every batch-handling path opens `logger.BeginScope(new { batchId, assetId })`, so all nested log lines carry the correlation id without passing it around. Message templates use structured placeholders only.

### Migrations
Add `deploy/sql/migrations/NNN_<name>.sql`, each idempotent (`IF NOT EXISTS`, `if_not_exists => true`). This change adds `001_rollups_and_retention.sql`:
- `CREATE MATERIALIZED VIEW IF NOT EXISTS measurement_1m WITH (timescaledb.continuous) AS SELECT time_bucket('1 minute', time) AS bucket, tag_id, min(value) AS min_value, max(value) AS max_value, avg(value) AS avg_value, count(*) AS sample_count FROM measurement WHERE quality = 0 GROUP BY bucket, tag_id WITH NO DATA;`
- `add_continuous_aggregate_policy('measurement_1m', start_offset => '1 hour', end_offset => '1 minute', schedule_interval => '1 minute', if_not_exists => true)`
- `add_retention_policy('measurement', INTERVAL '30 days', if_not_exists => true)` and `add_retention_policy('measurement_1m', INTERVAL '1 year', if_not_exists => true)`.

Applied by a one-shot `db-migrate` compose service (postgres image, `psql -f` over the migrations folder in order, `depends_on: timescaledb healthy`). On a fresh volume `init.sql` runs first, then the migrations. Alternative: a migration library in the transformer (DbUp/EF) — rejected for now; plain SQL keeps the schema reviewable and DBA-friendly, and the transformer should not own DDL.
Note: the rollups' 1-minute refresh lag must stay below the dashboard's "short range" threshold so recent minutes come from raw data.

### Dashboard
`deploy/grafana/dashboards/telemetrix.json`, built in the Grafana UI and exported. Panels: per-tag time series (asset variable; query switches to `measurement_1m` when `$__interval >= 1m`), pump status (state timeline on the `Running` tag), gaps (table + annotations from a new `plant_event` table, which the transformer fills by consuming `plant.events`; M5 only produces the events). Trends use `connect nulls: never` so gaps stay visible.

### Load test
- Simulator side: OPC PLC flags `--fn`/`--fr` (fast nodes, rate) or PlantSim's line-clone count, sized to 5,000 samples/s (e.g. 715 lines × 7 tags at 1 Hz with PlantSim).
- Measurement: `SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY ingested_at - time) FROM measurement WHERE time > now() - interval '30 minutes'` for NFR-06; consumer-lag metric / Kafka UI over 15 minutes for NFR-07.
- Results recorded in `docs/load-test.md` with machine specs, settings and numbers.
- D5: run NFR-06 at 500/5 s and at least one alternative (e.g. 1,000/2 s); record the chosen thresholds and why in a new ADR.

## Risks / Trade-offs

- [High-cardinality labels] → only `asset`, `source`, `partition`, `reason`; never tag or batchId as a metric label.
- [Lag gauge calls the broker] → sample every 10 s from a background loop, not per scrape.
- [Laptop cannot reach 5,000 samples/s with all containers] → record the ceiling and the bottleneck honestly in `docs/load-test.md`; tune batch size, partitions and COPY size before declaring NFR-07 failed.
- [Retention removes test data in long-running dev volumes] → expected; documented in the migration header.

## Open Questions

- Final batch thresholds (D5) — answered by the load test in this change; does not alter the specs.
