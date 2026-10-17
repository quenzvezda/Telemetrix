# Tasks

## 1. Dependencies and hosting

- [ ] 1.1 Add OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Exporter.Prometheus.AspNetCore`) to `Directory.Packages.props`; verify `dotnet build` succeeds
- [ ] 1.2 Move bridge and transformer to a slim web host with a configurable HTTP port, keeping the existing workers; verify both start and serve `GET /` 404 on their port

## 2. Health and logging

- [ ] 2.1 Add `/health` with the source/Kafka checks (bridge) and Kafka/database checks (transformer), buffer non-empty = Degraded; verify integration tests with Testcontainers return 200 when up and non-200 when Kafka is stopped
- [ ] 2.2 Configure JSON console logging with scopes and open a `batchId`/`assetId` scope around every batch path; verify a unit test with a capturing logger sees `batchId` on nested entries
- [ ] 2.3 Verify NFR-12 manually: produce one batch, grep both services' logs for its `batchId`, and see produce → consume → write in order

## 3. Metrics

- [ ] 3.1 Add the `Telemetrix.Bridge` meter with samples received, batches produced and buffer depth; verify a `MeterListener` unit test observes each instrument with the expected labels
- [ ] 3.2 Add the `Telemetrix.Transformer` meter with rows written, dead-lettered batches, write duration and a 10 s consumer-lag sampler; verify an integration test sees lag grow while the consumer is paused
- [ ] 3.3 Expose Prometheus scrape when `Telemetry:Prometheus:Enabled=true` and add an optional `prometheus` service under the `metrics` compose profile; verify `docker compose --profile metrics up` and see the `telemetrix_*` series in Prometheus

## 4. Rollups, retention, migrations

- [ ] 4.1 Add `deploy/sql/migrations/001_rollups_and_retention.sql` (continuous aggregate, refresh policy, both retention policies, all idempotent); verify it runs twice in a row without error on a fresh database
- [ ] 4.2 Add the one-shot `db-migrate` compose service; verify on an existing M7 volume that rollups and policies appear and the `measurement` row count is unchanged
- [ ] 4.3 Add an integration test (TimescaleDB Testcontainer) inserting 10/20/30 Good and 999 Bad in one minute, refreshing the aggregate, and asserting min 10 / max 30 / avg 20 / count 3
- [ ] 4.4 Verify retention policies with `SELECT * FROM timescaledb_information.jobs` showing 30 days on `measurement` and 1 year on `measurement_1m`

## 5. Dashboard

- [ ] 5.1 Build the Telemetrix dashboard (asset variable, per-tag trends switching to rollups for long ranges, pump state timeline, gaps table and annotations) and export it to `deploy/grafana/dashboards/telemetrix.json`; verify it loads after `docker compose up` on a fresh Grafana
- [ ] 5.2 Add a `plant_event` table (migration) and a transformer consumer of `plant.events` that fills it, for the gaps panel; verify restarting `opc-plc` for 60 s shows a gap with start and end time and a break in the trends
- [ ] 5.3 Verify pump status by tripping `Pump01` (PlantSim fault injection) and seeing it shown as stopped within one refresh

## 6. Load test and milestone verification

- [ ] 6.1 Script the load profile (simulator scale-up flags or PlantSim line clones) in `deploy/` and document it in `docs/load-test.md`; verify the script reaches ~5,000 samples/s on the samples-received metric
- [ ] 6.2 Run 30 minutes at default settings and record p95 of `ingested_at - time`; verify it is below 10 s (NFR-06)
- [ ] 6.3 Run 15 minutes at 5,000 samples/s and record consumer lag; verify no upward trend and zero dead-lettered batches (NFR-07)
- [ ] 6.4 Compare batch thresholds (500/5 s vs at least one alternative), record the decision as a new ADR in `docs/adr/` and update D5 in `ROADMAP.md`; verify the ADR and ROADMAP row exist
- [ ] 6.5 Verify the M8 "Done when": the dashboard shows trends, pump state and gaps, and the NFR-06 / NFR-07 results in `docs/load-test.md` pass
