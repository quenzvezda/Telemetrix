# Proposal

## Why

After M7 the pipeline works but is a black box: we cannot see throughput, lag, buffer depth or latency, and nothing proves it meets NFR-06 (p95 < 10 s) or NFR-07 (5,000 samples/s). Dashboards also need rollups so trends do not scan raw data, and raw data must not grow forever.

Milestone: M8 — Observability and load.

## What Changes

- FR-41: every service exposes `/health` and OpenTelemetry metrics — samples in, batches out, buffer depth, consumer lag, rows written.
- NFR-12: structured JSON logs carry `batchId` as a correlation id across bridge and transformer.
- FR-31: a 1-minute continuous aggregate (min, max, avg, count) of Good measurements.
- FR-32: raw measurements are dropped after 30 days, rollups after 1 year.
- FR-40: a provisioned Grafana dashboard with live trends per tag, pump status and data gaps.
- NFR-06 / NFR-07: measured with a repeatable load test; open decision D5 (batch thresholds) is resolved from the measurements and recorded as an ADR.

## Capabilities

### New Capabilities
- `observability`: health endpoints, pipeline metrics and correlated structured logs for every service.
- `dashboard`: the operator-facing view of trends, pump state and data gaps.

### Modified Capabilities
- `telemetry-storage`: adds rollups, retention and migrations (delta uses ADDED because the base capability is not archived yet).

## Impact

- Code: `Telemetrix.Edge.Bridge`, `Telemetrix.Transformer` (plus PlantSim/MqttSim health) — metrics, health checks, logging configuration.
- Database: new migration under `deploy/sql/` for the continuous aggregate and policies.
- Infrastructure: dashboard JSON in `deploy/grafana/dashboards/`; optional Prometheus service in `deploy/docker-compose.yml`.
- Dependencies: OpenTelemetry .NET packages, ASP.NET Core health checks.
- Docs: new ADR for batch thresholds (D5); ROADMAP open-decision table updated.
