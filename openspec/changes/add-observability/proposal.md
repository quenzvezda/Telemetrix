# Proposal

## Why

After M7 the pipeline works but is a black box: we cannot see throughput, lag, buffer depth or latency, and nothing proves it meets NFR-06 (p95 < 10 s) or NFR-07 (5,000 samples/s). Dashboards also need rollups so trends do not scan raw data, and raw data must not grow forever.

Milestone: M8 — Observability and load.

## What Changes

- FR-41: Bridge, Transformer, PlantSim and MqttSim expose `/health`; OpenTelemetry pipeline metrics cover samples received, batches produced, buffer depth, consumer lag and measurements written where applicable.
- NFR-12: all four services write structured JSON logs; batch-handling logs carry `batchId` as a correlation id across Bridge and Transformer.
- FR-31: a 1-minute continuous aggregate (min, max, avg, count) of Good measurements.
- FR-32: raw measurements are dropped after 30 days, rollups after 1 year.
- FR-40: a provisioned Grafana dashboard with live trends per tag, pump status and data gaps.
- NFR-06 / NFR-07: measured with a repeatable load test using the reviewer-approved baseline of 500 samples / 5 seconds per asset, whichever comes first. D5 remains open until the baseline and at least one alternative have been measured; the reviewer selects the final thresholds and the outcome is recorded as an ADR.

## Capabilities

### New Capabilities
- `observability`: health endpoints, pipeline metrics and correlated structured logs for every service.
- `dashboard`: the operator-facing view of trends, pump state and data gaps.

### Modified Capabilities
- `telemetry-storage`: adds rollups, retention and migrations to the main capability archived in M3; these new requirements use ADDED deltas.

## Impact

- Code: `Telemetrix.Edge.Bridge`, `Telemetrix.Transformer`, `Telemetrix.PlantSim` and `Telemetrix.MqttSim` — HTTP hosting, health checks and JSON logging; pipeline metrics in Bridge and Transformer.
- Database: reviewable migrations under `deploy/sql/` for the continuous aggregate, policies and durable plant-event history.
- Infrastructure: dashboard JSON in `deploy/grafana/dashboards/`; optional Prometheus service and an explicitly invoked migration service in `deploy/docker-compose.yml`. The reviewer reviews SQL before running migrations against the local database.
- Dependencies: OpenTelemetry .NET packages, ASP.NET Core health checks.
- Docs: new ADR for batch thresholds (D5); ROADMAP open-decision table updated.
