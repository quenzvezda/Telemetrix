# M8 review package

Change: `add-observability`. Implementation is in progress; OpenSpec remains open.
The reviewer authorized all task groups on 2026-10-25. Final D5 selection and
milestone acceptance remain reviewer checkpoints after measurements are available.

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
