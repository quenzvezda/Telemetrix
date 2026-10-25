# MQTT pumping-line simulator

MqttSim is a separate process using the same `Telemetrix.Simulation` model and
seven tag definitions as PlantSim. It publishes one sample per tag per second to
`plant/Plant01/Line01/{asset}/{tag}` with MQTT 3.1.1, QoS 1 and retain enabled.

From the repository root, with the development Mosquitto service running:

```powershell
dotnet run --project src/Telemetrix.MqttSim -c Release
```

Or build and run the container (requires `deploy/.env` for compose interpolation):

```powershell
docker compose -f deploy/docker-compose.yml up -d --build mqttsim
docker compose -f deploy/docker-compose.yml exec -T mosquitto mosquitto_sub -t 'plant/#' -q 1 -v -C 70 -W 20
```

Expect seven topics, numeric values and Boolean `Pump01/Running`, UTC
`sourceTimestamp`, and `quality: good`. Retained values arrive immediately;
then each topic receives one fresh value every second. Set
`--MqttSim:BadQualityTag=Tank01.Temperature` to publish that tag with bad quality.

| Key under `MqttSim` | Default |
| --- | --- |
| `Host` | `localhost` (`mosquitto` in compose) |
| `Port` | `1883` |
| `ClientId` | `telemetrix-mqttsim` (must be unique per running instance) |
| `Plant` / `Line` | `Plant01` / `Line01` |
| `Seed` | `1729` |
| `BadQualityTag` | unset; optional `Asset.Tag` from the shared catalog |

Use command-line keys as above, environment variables such as `MqttSim__Host`,
or .NET configuration files. Stop with Ctrl+C or `docker compose ... stop mqttsim`.
Startup and network failures retry with capped exponential backoff. A pending
sample keeps its original timestamp until acknowledged; source history is not
persisted across simulator process termination. Anonymous, unencrypted MQTT is
for local development only.

The HTTP listener uses port 5104, exposed by Compose. Override it with
`--Http:Port=5104` or `Http__Port`; update the container port mapping to match.
