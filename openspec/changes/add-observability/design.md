# Design

## Context

Bridge, Transformer, PlantSim and MqttSim are Worker Services with no HTTP surface. `deploy/sql/init.sql` creates the schema but runs only when the `tsdata` volume is empty, so anything added now must also reach databases created in M1–M7. Grafana is already provisioned with the TimescaleDB data source (`uid: timescaledb`) and a file dashboard provider reading `deploy/grafana/dashboards/`.

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
Switch Bridge, Transformer, PlantSim and MqttSim from `Host.CreateApplicationBuilder` to `WebApplication.CreateSlimBuilder` so they can serve `/health` on configurable ports, keeping their hosted workers. Checks: Bridge — the configured OPC UA or MQTT source connected and Kafka metadata reachable; Transformer — Kafka metadata reachable and `SELECT 1` on TimescaleDB; PlantSim — OPC UA server started and simulation loop running; MqttSim — MQTT broker connected and publishing loop running. Starting or failed critical dependencies report Unhealthy with HTTP 503. With critical dependencies healthy, a non-empty Bridge buffer reports Degraded with HTTP 200; an empty buffer reports Healthy with HTTP 200. Health checks observe state without reconnecting, publishing samples or changing collection behaviour.
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

Consumer lag is computed per assigned partition as the high watermark minus the group's committed next offset, sampled every 10 s without racing the consumer's poll loop. Lag verification pauses consumption while the Transformer process and its metrics endpoint stay alive; Kafka UI provides lag visibility while the process is stopped. Exporters: console in development, Prometheus scrape endpoint (`OpenTelemetry.Exporter.Prometheus.AspNetCore`) on Bridge and Transformer when `Telemetry:Prometheus:Enabled=true`; an optional `prometheus` service is added to compose under a `metrics` profile so the default `docker compose up` stays lean.

### Logging
Configure `AddJsonConsole` with scopes enabled in all four services. Every Bridge and Transformer batch-handling path opens a structured scope containing `batchId` and `assetId`, so nested log entries carry searchable correlation properties. Verify the serialized JSON as well as the captured scope. Message templates use structured placeholders only; PlantSim and MqttSim do not invent batch identifiers.

### Migrations
Add `deploy/sql/migrations/NNN_<name>.sql`, each idempotent (`IF NOT EXISTS`, `if_not_exists => true`). This change adds `001_rollups_and_retention.sql`:
- `CREATE MATERIALIZED VIEW IF NOT EXISTS measurement_1m WITH (timescaledb.continuous) AS SELECT time_bucket('1 minute', time) AS bucket, tag_id, min(value) AS min_value, max(value) AS max_value, avg(value) AS avg_value, count(*) AS sample_count FROM measurement WHERE quality = 0 GROUP BY bucket, tag_id WITH NO DATA;`
- `add_continuous_aggregate_policy('measurement_1m', start_offset => '1 hour', end_offset => '1 minute', schedule_interval => '1 minute', if_not_exists => true)`
- `add_retention_policy('measurement', INTERVAL '30 days', if_not_exists => true)` and `add_retention_policy('measurement_1m', INTERVAL '1 year', if_not_exists => true)`.
- Backfill completed buckets from the oldest retained raw bucket to the last complete refresh boundary. The bounded start preserves older rollups when raw retention has already removed their source measurements.

Provide a one-shot `db-migrate` compose service under a `migrations` profile (postgres image, `psql -v ON_ERROR_STOP=1 -f` over the migrations folder in order, `depends_on: timescaledb healthy`). After reviewing the SQL, the reviewer explicitly runs `docker compose -f deploy/docker-compose.yml --profile migrations run --rm db-migrate`. Default stack startup does not apply migrations; on a fresh volume `init.sql` runs first, then the reviewer runs migrations. Automated mutation checks use disposable TimescaleDB Testcontainers; existing local volumes are checked manually by the reviewer. Alternative: a migration library in the Transformer (DbUp/EF) — rejected for now; plain SQL keeps the schema reviewable and the Transformer does not own DDL.
Note: the rollups' 1-minute refresh lag must stay below the dashboard's "short range" threshold so recent minutes come from raw data.

### Dashboard
`deploy/grafana/dashboards/telemetrix.json` is a provisioned dashboard verified in a fresh Grafana and its export view. Asset and tag variables produce one trend panel per selected tag, with matching units. Queries use raw Good measurements below six hours and `measurement_1m` for longer ranges. Other panels show the `Running` state timeline, current state with its last observed change and sample, and stored gaps as a table and range annotations. Explicit null markers at gap boundaries and `spanNulls=false` prevent interpolation; the state timeline resumes from the first Good Running measurement after each gap.

### Plant event storage
The plant-event storage consumer uses its own `transformer-events` group and commits offsets only after an idempotent `event_id` insert (the same database-before-offset principle as ADR-0003). Bridge retains topic ownership (ADR-0007). Enable it with `PlantEvents:Enabled=true` after the reviewer applies the SQL migrations; it is opt-in so an existing M7 database can keep running before migration review. The dashboard review profile enables this consumer on a migrated disposable database.

### Load test
- The reviewer approved 500 samples / 5 seconds per asset, whichever comes first, as the M8 baseline on 2026-10-25. Keep the defaults while implementing observability; this approval allows M8 to start with D5 still open.
- Simulator side: OPC PLC flags `--fn`/`--fr` (fast nodes, rate) or PlantSim's line-clone count, sized to 5,000 samples/s (e.g. 715 lines × 7 tags at 1 Hz with PlantSim).
- Measurement: `SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY ingested_at - time) FROM measurement WHERE time > now() - interval '30 minutes'` for NFR-06; consumer-lag metric / Kafka UI over 15 minutes for NFR-07.
- Results recorded in `docs/load-test.md` with machine specs, settings and numbers.
- Use the default seven-tag PlantSim OPC UA source for latency. The throughput profile uses MqttSim's shared pumping-line model with 150 uniquely named clones at 5 Hz (5,250 offered samples/s), QoS 1 and at most 64 concurrent publications. This gives the generator 5% headroom for timer scheduling jitter while acceptance requires at least 5,000 actually received samples/s. Seed the disposable asset/tag catalog before starting Transformer. Every tick completes before the next, preserving each tag's order. Compare acknowledged source publications with received samples and final stored measurements, so source-broker drops cannot be masked by low Kafka lag.
- Keep `Ingestion:BatchQueueCapacity=64` by default; the throughput profile explicitly uses 1,024 bounded batches to absorb synchronized per-asset flushes. A red/green burst regression and the load smoke reproduced and removed the source admission stall at the old fixed capacity. Both threshold alternatives use the same queue capacity. Buffer quotas and overflow behaviour remain unchanged.
- Size the disposable throughput broker's outgoing queue explicitly to 100,000 messages for both candidates. The default 1,000-message queue dropped messages in a full baseline run despite stable Kafka lag. Record the broker capacity and retain mandatory source-acknowledgement/storage equality; a larger broker queue alone cannot establish throughput acceptance. Ordinary development broker settings remain unchanged.
- D5 resolved on 2026-10-25: after both candidates passed the full-duration profiles, the reviewer selected 500 samples / 5 seconds per asset, whichever comes first (ADR-0015). At the default rate its p95 was 7,774.686 ms; throughput was 5,250.343 samples/s with complete drain and zero dead letters. High-rate p95 was 7,644.455 ms versus 21,240.268 ms for 1,000/2 s. The selected thresholds match existing defaults, so no configuration/requirement change or repeated acceptance run is needed. Evidence and host-contention limits are recorded in `docs/load-test.md`.

## Risks / Trade-offs

- [High-cardinality labels] → only `asset`, `source`, `partition`, `reason`; never tag or batchId as a metric label.
- [Lag gauge calls the broker] → sample every 10 s from a background loop, not per scrape.
- [Laptop cannot reach 5,000 samples/s with all containers] → record the ceiling and the bottleneck honestly in `docs/load-test.md`; tune batch size, partitions and COPY size before declaring NFR-07 failed.
- [Retention removes test data in long-running dev volumes] → expected; documented in the migration header.

## Open Questions

- No open batching decision remains: D5 is accepted in ADR-0015. Existing-volume migration verification and final manual M8 acceptance remain reviewer checkpoints before archival.
