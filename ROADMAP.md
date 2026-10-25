# Roadmap

Nine milestones, each ending in something you can see working. A thin path runs end to end by M3; M4–M9 harden it. Build the client side before the simulator or dashboards.

Status values: Not started → In progress → In review → Done.

Each milestone is one OpenSpec change under `openspec/changes/`. Its `tasks.md` is the working checklist; the milestone is **done** when the change is archived (`/opsx:archive`), which merges its requirements into `openspec/specs/`.

Requirement IDs (FR-xx / NFR-xx) are defined in [docs/plant-telemetry-pipeline.md](docs/plant-telemetry-pipeline.md).

## Milestones

| # | Milestone | OpenSpec change | Covers | Done when | Status |
| --- | --- | --- | --- | --- | --- |
| M1 | Environment | `add-local-environment` | NFR-11 (infra), NFR-10 | Values change in UaExpert; a topic can be created in Kafka UI; `measurement` exists in TimescaleDB | Done |
| M2 | OPC UA client | `add-opcua-subscription` | FR-10, FR-11, FR-12 | Values print every second with source timestamp and status code; the client resumes after the OPC PLC container restarts | Done |
| M3 | First end-to-end slice | `add-end-to-end-slice` | FR-13, FR-14, FR-22, FR-23, FR-30 | Rows appear in `measurement` and a `SELECT` shows the live trend | Done |
| M4 | Delivery guarantees | `add-delivery-guarantees` | FR-20, FR-21, FR-24, NFR-01, NFR-03, NFR-05, NFR-08 | Transformer killed under load; row count still equals samples sent | Done |
| M5 | Edge resilience | `add-edge-store-and-forward` | FR-15, FR-16, NFR-02, NFR-04 | Kafka down 30 min drains with zero loss; OPC UA restart reconnects < 30 s and logs the gap | Done |
| M6 | Own OPC UA server | `add-plant-simulator` | FR-01, FR-02 | Bridge switches from OPC PLC to PlantSim by configuration only | Done |
| M7 | MQTT input | `add-mqtt-source` | FR-03, FR-17 | Same tags land in the database from MQTT with no transformer change | In progress |
| M8 | Observability and load | `add-observability` | FR-31, FR-32, FR-40, FR-41, NFR-06, NFR-07, NFR-12 | Dashboard shows trends, pump state and gaps; p95 latency < 10 s; 5,000 samples/s without growing lag | Not started |
| M9 | Security | `add-opcua-security` | NFR-09, NFR-10 | An untrusted client certificate is rejected; no secret in the repo | Not started |

M3 was accepted and archived on 2026-10-18 with all 14 tasks complete. Its four capabilities are in main specs; see the [archived change](openspec/changes/archive/2026-10-18-add-end-to-end-slice/) and [review evidence](docs/m3-group6-review.md). The observed stopped-consumer backlog remains documented; zero lag and M4 delivery guarantees are not claimed.

M4 was accepted by the reviewer and archived on 2026-10-22 with all 13 tracked tasks complete. Release CI passed all 137 tests and strict OpenSpec validation; the production Transformer SIGKILL check stored all 20,000 samples with zero lag. Its requirements are in main specs; see the [archived change](openspec/changes/archive/2026-10-22-add-delivery-guarantees/) and [review package](docs/m4-review.md).

M5 was accepted by the reviewer and merged through [PR #2](https://github.com/quenzvezda/Telemetrix/pull/2). Its OpenSpec change was archived on 2026-10-24 with all 15 tracked tasks complete and its requirements synced into main specs. D3 is resolved in ADR-0013. Release CI passed all 164 tests, strict OpenSpec validation, the M4 SIGKILL regression and the short M5 outage check. The full Kafka outage lasted 1,801.140 measured seconds and drained all 12,880 samples with an empty buffer and zero lag. Production OPC UA-to-Kafka recovery restored all seven fresh tags and ordered plant events within 30 seconds. See the [review package](docs/m5-review.md) and [archived change](openspec/changes/archive/2026-10-24-add-edge-store-and-forward/).

M6 was accepted by the reviewer and archived on 2026-10-24 with all 13 tracked tasks complete. Its seven requirements are synced into main specs. PlantSim exposes the seven typed tags, manual and seeded random faults, a production Docker image and a Bridge configuration profile. All 182 Release tests passed locally; CI build/test, strict OpenSpec, M4 SIGKILL and M5 edge-outage jobs passed. Container end-to-end verification stored all seven tags and delivered ordered recovery/gap events within the unchanged NFR-04 deadline. UaExpert checks confirmed the tags and all five fault methods. See the [review package](docs/m6-review.md), [archived change](openspec/changes/archive/2026-10-24-add-plant-simulator/) and [PR #3](https://github.com/quenzvezda/Telemetrix/pull/3).

NFR-13 (tests) is not a milestone: every change lands its own unit and integration tests, and CI (`.github/workflows/ci.yml`) stays green.

## Open decisions

Decide before the milestone that needs it starts; record the outcome as an ADR in `docs/adr/` and update the change's specs.

| # | Decision | Needed by | Leaning |
| --- | --- | --- | --- |
| D5 | Batch thresholds: keep 500 samples / 5 s, or tune after measuring latency? | M8 | Keep, re-measure under NFR-06 |

## Resolved decisions

- D3 (needed by M5): configurable buffer sized for at least one hour at default load; pause collection on overflow, preserve accepted batches and report the collection gap. Accepted in [ADR-0013](docs/adr/0013-bound-edge-buffer-and-pause-collection.md). The reviewer authorized autonomous implementation across all M5 task groups on 2026-10-22.

- D4 (needed by M4): defer `plant.telemetry.clean` (FR-25) to the separate-sink stretch goal. Accepted in [ADR-0012](docs/adr/0012-defer-clean-topic-to-separate-sink.md).

- D1 (needed by M3): `dead-letter` is the unknown-tag default; `create` is an explicit opt-in. Accepted in [ADR-0011](docs/adr/0011-dead-letter-unknown-tags-by-default.md).
- D2 (needed by M2/M3): numeric sample values, booleans as 1/0 from the source adapter, strings out of scope for v1. Accepted in [ADR-0010](docs/adr/0010-numeric-sample-values-in-v1.md).

## Stretch goals (after M9)

Schema registry (Avro/Protobuf), separate DB sink, stream aggregation and `plant.alarms`, OPC UA deadband (report by exception), Sparkplug B, operator write-back via OPC UA method call, three-broker Kafka cluster. Details in the design doc.
