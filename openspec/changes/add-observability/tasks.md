# Tasks

Planning baseline approved by the reviewer on 2026-10-25: 500 samples / 5 seconds per asset, whichever comes first. D5 remains open until the load-test comparison in task 6.4. Stop for reviewer feedback after each task group; stop at task 6.4 for the final D5 choice before changing defaults or completing milestone acceptance.

## 1. Dependencies and hosting

- [x] 1.1 Add OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Exporter.Prometheus.AspNetCore`) to `Directory.Packages.props`; verify `dotnet build` succeeds
- [x] 1.2 Move bridge and transformer to a slim web host with a configurable HTTP port, keeping the existing workers; verify both start and serve `GET /` 404 on their port
- [x] 1.3 Move PlantSim and MqttSim to slim web hosts with configurable HTTP ports; update container port/configuration documentation and verify both start, serve `GET /` 404 and still expose seven OPC UA tags / publish seven MQTT tags at the default rate

## 2. Health and logging

- [x] 2.1 Add `/health` with the configured OPC UA or MQTT source/Kafka checks (Bridge) and Kafka/database checks (Transformer); verify integration tests with Testcontainers return 200/Healthy when dependencies are up, 503 naming a failed dependency when down, and 200/Degraded when a non-empty buffer drains with dependencies healthy
- [x] 2.2 Configure JSON console logging with scopes in Bridge, Transformer, PlantSim and MqttSim; open a structured `batchId`/`assetId` scope around every Bridge/Transformer batch path; verify a capturing-logger test sees the scope on nested entries and emitted JSON parses with the correlation properties
- [x] 2.3 Verify NFR-12 manually: produce one batch, grep both services' logs for its `batchId`, and see produce → consume → write in order
- [x] 2.4 Add `/health` to PlantSim (OPC UA server and simulation loop) and MqttSim (broker connection and publishing loop); verify startup/worker-state checks, then use a Mosquitto Testcontainer to verify MqttSim returns 503 on broker loss and recovers to 200 without disrupting sample publication

## 3. Metrics

- [x] 3.1 Add the `Telemetrix.Bridge` meter with samples received, batches produced and buffer depth; verify a `MeterListener` unit test observes each instrument with the expected labels
- [x] 3.2 Add the `Telemetrix.Transformer` meter with rows written, dead-lettered batches, write duration and a 10 s consumer-lag sampler; verify an integration test sees lag grow while the consumer is paused
- [ ] 3.3 Expose Bridge/Transformer Prometheus scrape when `Telemetry:Prometheus:Enabled=true` and add an optional `prometheus` service under the `metrics` compose profile; verify `docker compose -f deploy/docker-compose.yml --profile metrics up -d` and see the `telemetrix_*` series in Prometheus

## 4. Rollups, retention, migrations

- [ ] 4.1 Add `deploy/sql/migrations/001_rollups_and_retention.sql` (continuous aggregate, refresh policy, both retention policies, all idempotent); verify it runs twice in a row without error on a fresh database
- [ ] 4.2 Add the one-shot `db-migrate` compose service under an opt-in `migrations` profile with SQL errors stopping execution; document the SQL review and manual command `docker compose -f deploy/docker-compose.yml --profile migrations run --rm db-migrate`; verify default compose startup does not run it, and have the reviewer verify on an existing M7 volume that rollups and policies appear and the stored measurement count is unchanged
- [ ] 4.3 Add an integration test (TimescaleDB Testcontainer) inserting 10/20/30 Good and 999 Bad in one minute, refreshing the aggregate, and asserting min 10 / max 30 / avg 20 / count 3
- [ ] 4.4 Verify retention policies with `SELECT * FROM timescaledb_information.jobs` showing 30 days on `measurement` and 1 year on `measurement_1m`

## 5. Dashboard

- [ ] 5.1 Add a `plant_event` table (reviewable migration) and a Transformer consumer of `plant.events` for the gaps panel; verify with Testcontainers that connection-lost, connection-restored and gap events are stored and replay does not duplicate events
- [ ] 5.2 Build the Telemetrix dashboard (asset variable, per-tag trends switching to rollups for long ranges, pump state timeline, gaps table and annotations) and export it to `deploy/grafana/dashboards/telemetrix.json`; after reviewer-applied migrations, verify it loads on a fresh Grafana and a 60 s PlantSim outage shows a gap with start/end time and a break in trends
- [ ] 5.3 Verify pump status by tripping `Pump01` (PlantSim fault injection) and seeing it shown as stopped within one refresh

## 6. Load test and milestone verification

- [ ] 6.1 Script the load profile (simulator scale-up flags or PlantSim line clones) in `deploy/` and document it in `docs/load-test.md`; verify the script reaches ~5,000 samples/s on the samples-received metric
- [ ] 6.2 Run 30 minutes at the default simulation rate with the 500 samples / 5 seconds baseline and record the run window and p95 of `ingested_at - time` in `docs/load-test.md`; verify p95 is below 10 s (NFR-06)
- [ ] 6.3 Run 15 minutes at 5,000 samples/s with the 500 samples / 5 seconds baseline; record actual throughput, the run window and consumer lag in `docs/load-test.md`; verify no upward lag trend and zero dead-lettered batches (NFR-07)
- [ ] 6.4 Measure at least one alternative (e.g. 1,000 samples / 2 seconds) using the same machine, 30-minute latency profile and 15-minute throughput profile; present the comparison and wait for the reviewer's D5 choice; record the accepted thresholds and evidence as a new ADR in `docs/adr/` and update D5 in `ROADMAP.md`; verify the ADR cites the measured settings/results and reviewer decision
- [ ] 6.5 Verify the M8 "Done when" at the reviewer-selected defaults: update the planning artifacts and batching requirement before implementing any default change, repeat the NFR-06 / NFR-07 acceptance runs if defaults changed, and hand the reviewer dashboard checks for trends, pump state and gaps plus the passing `docs/load-test.md` results; verify D5 is resolved and the reviewer accepts the manual checks before archiving
