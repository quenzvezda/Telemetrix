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
