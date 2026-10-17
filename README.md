# Telemetrix

A plant telemetry pipeline in C# / .NET 10: a simulated plant exposes sensor values over OPC UA, an edge bridge batches them and publishes to Kafka, a transformer cleans and enriches them, and TimescaleDB stores the result for Grafana.

```text
OPC PLC / PlantSim ──OPC UA──┐
MqttSim ──────────MQTT───────┤
                             ▼
                      Edge.Bridge ──► Kafka ──► Transformer ──► TimescaleDB ──► Grafana
                      (batch, buffer)  plant.telemetry.raw     (COPY + ON CONFLICT)
```

Central guarantee: no sample lost and no duplicate row, even when any single service is killed mid-batch.

## Repository map

| Path | What |
| --- | --- |
| `docs/plant-telemetry-pipeline.md` | Design document: architecture, data model, FR/NFR requirements |
| `CONTEXT.md` | Domain glossary |
| `docs/adr/` | Architecture decision records |
| `ROADMAP.md` | Milestones M1–M9 and open decisions |
| `openspec/` | Spec-driven changes, one per milestone (`changes/`) and accepted specs (`specs/`) |
| `src/Telemetrix.Contracts` | Shared message records and JSON options |
| `src/Telemetrix.Edge.Bridge` | Edge worker: OPC UA/MQTT sources, batching, SQLite buffer, Kafka producer |
| `src/Telemetrix.Transformer` | Consumer worker: validate, quality, enrich, idempotent bulk write |
| `src/Telemetrix.PlantSim` | Own OPC UA server modelling the pumping line (M6) |
| `src/Telemetrix.MqttSim` | MQTT publisher of the same tags (M7) |
| `tests/` | Unit tests per service; Testcontainers integration tests |
| `deploy/` | Docker Compose stack, database init SQL, Grafana provisioning |

## Prerequisites

- .NET SDK 10.0.300+ (`global.json`)
- Docker Desktop
- UaExpert (free OPC UA client) for browsing the server
- Node.js 22+ only if you use the OpenSpec CLI (`npx @fission-ai/openspec`)

## Local environment

```sh
cp deploy/.env.example deploy/.env        # set POSTGRES_PASSWORD
docker compose -f deploy/docker-compose.yml up -d
dotnet build
dotnet test
```

| Service | Address |
| --- | --- |
| OPC PLC (OPC UA, security None) | `opc.tcp://localhost:50000` |
| Mosquitto (MQTT) | `localhost:1883` |
| Kafka (host / containers) | `localhost:29092` / `kafka:9092` |
| Kafka UI | http://localhost:8080 |
| TimescaleDB | `localhost:5432`, database `telemetry`, user `telemetry` |
| Grafana | http://localhost:3000 (admin / admin on first login) |

Reset everything, including Kafka topics and database data: `docker compose -f deploy/docker-compose.yml down -v`.

## Working on a milestone

See `ROADMAP.md` for the current milestone, then follow its OpenSpec change:

```sh
npx @fission-ai/openspec list                 # in-flight changes and task progress
npx @fission-ai/openspec show add-local-environment
```

In Claude Code: `/opsx:apply <change>` to implement, `/opsx:archive <change>` when the milestone's "Done when" passes.
