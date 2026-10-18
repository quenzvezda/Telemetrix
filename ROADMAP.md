# Roadmap

Nine milestones, each ending in something you can see working. A thin path runs end to end by M3; M4–M9 harden it. Build the client side before the simulator or dashboards.

Status values: Not started → In progress → In review → Done.

Each milestone is one OpenSpec change under `openspec/changes/`. Its `tasks.md` is the working checklist; the milestone is **done** when the change is archived (`/opsx:archive`), which merges its requirements into `openspec/specs/`.

Requirement IDs (FR-xx / NFR-xx) are defined in [docs/plant-telemetry-pipeline.md](docs/plant-telemetry-pipeline.md).

## Milestones

| # | Milestone | OpenSpec change | Covers | Done when | Status |
| --- | --- | --- | --- | --- | --- |
| M1 | Environment | `add-local-environment` | NFR-11 (infra), NFR-10 | Values change in UaExpert; a topic can be created in Kafka UI; `measurement` exists in TimescaleDB | Done |
| M2 | OPC UA client | `add-opcua-subscription` | FR-10, FR-11, FR-12 | Values print every second with source timestamp and status code; the client resumes after the OPC PLC container restarts | Not started |
| M3 | First end-to-end slice | `add-end-to-end-slice` | FR-13, FR-14, FR-22, FR-23, FR-30 | Rows appear in `measurement` and a `SELECT` shows the live trend | Not started |
| M4 | Delivery guarantees | `add-delivery-guarantees` | FR-20, FR-21, FR-24, NFR-01, NFR-03, NFR-05, NFR-08 | Transformer killed under load; row count still equals samples sent | Not started |
| M5 | Edge resilience | `add-edge-store-and-forward` | FR-15, FR-16, NFR-02, NFR-04 | Kafka down 30 min drains with zero loss; OPC UA restart reconnects < 30 s and logs the gap | Not started |
| M6 | Own OPC UA server | `add-plant-simulator` | FR-01, FR-02 | Bridge switches from OPC PLC to PlantSim by configuration only | Not started |
| M7 | MQTT input | `add-mqtt-source` | FR-03, FR-17 | Same tags land in the database from MQTT with no transformer change | Not started |
| M8 | Observability and load | `add-observability` | FR-31, FR-32, FR-40, FR-41, NFR-06, NFR-07, NFR-12 | Dashboard shows trends, pump state and gaps; p95 latency < 10 s; 5,000 samples/s without growing lag | Not started |
| M9 | Security | `add-opcua-security` | NFR-09, NFR-10 | An untrusted client certificate is rejected; no secret in the repo | Not started |

NFR-13 (tests) is not a milestone: every change lands its own unit and integration tests, and CI (`.github/workflows/ci.yml`) stays green.

## Open decisions

Decide before the milestone that needs it starts; record the outcome as an ADR in `docs/adr/` and update the change's specs.

| # | Decision | Needed by | Leaning |
| --- | --- | --- | --- |
| D1 | Unknown tags: auto-create in the database, or dead-letter? (FR-23 makes it configurable — which is the default?) | M3 | Dead-letter by default; auto-create opt-in |
| D2 | Booleans and strings: store as numbers in `measurement`, or a separate table for non-numeric values? | M3 | Booleans as 1/0; strings out of scope for v1 |
| D3 | Edge buffer limit: how many hours, and on overflow drop oldest or stop subscribing? | M5 | — |
| D4 | Keep `plant.telemetry.clean` (FR-25) in v1, or only with the separate sink? | M4 | Defer to the separate-sink stretch goal |
| D5 | Batch thresholds: keep 500 samples / 5 s, or tune after measuring latency? | M8 | Keep, re-measure under NFR-06 |

## Stretch goals (after M9)

Schema registry (Avro/Protobuf), separate DB sink, stream aggregation and `plant.alarms`, OPC UA deadband (report by exception), Sparkplug B, operator write-back via OPC UA method call, three-broker Kafka cluster. Details in the design doc.
