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
| M7 | MQTT input | `add-mqtt-source` | FR-03, FR-17 | Same tags land in the database from MQTT with no transformer change | Done |
| M8 | Observability and load | `add-observability` | FR-31, FR-32, FR-40, FR-41, NFR-06, NFR-07, NFR-12 | Dashboard shows trends, pump state and gaps; p95 latency < 10 s; 5,000 samples/s without growing lag | Done (manual checks deferred to M9) |
| M9 | Security | `add-opcua-security` | NFR-09, NFR-10 | An untrusted client certificate is rejected; no secret in the repo | In progress |

M3 was accepted and archived on 2026-10-18 with all 14 tasks complete. Its four capabilities are in main specs; see the [archived change](openspec/changes/archive/2026-10-18-add-end-to-end-slice/) and [review evidence](docs/m3-group6-review.md). The observed stopped-consumer backlog remains documented; zero lag and M4 delivery guarantees are not claimed.

M4 was accepted by the reviewer and archived on 2026-10-22 with all 13 tracked tasks complete. Release CI passed all 137 tests and strict OpenSpec validation; the production Transformer SIGKILL check stored all 20,000 samples with zero lag. Its requirements are in main specs; see the [archived change](openspec/changes/archive/2026-10-22-add-delivery-guarantees/) and [review package](docs/m4-review.md).

M5 was accepted by the reviewer and merged through [PR #2](https://github.com/quenzvezda/Telemetrix/pull/2). Its OpenSpec change was archived on 2026-10-24 with all 15 tracked tasks complete and its requirements synced into main specs. D3 is resolved in ADR-0013. Release CI passed all 164 tests, strict OpenSpec validation, the M4 SIGKILL regression and the short M5 outage check. The full Kafka outage lasted 1,801.140 measured seconds and drained all 12,880 samples with an empty buffer and zero lag. Production OPC UA-to-Kafka recovery restored all seven fresh tags and ordered plant events within 30 seconds. See the [review package](docs/m5-review.md) and [archived change](openspec/changes/archive/2026-10-24-add-edge-store-and-forward/).

M6 was accepted by the reviewer and archived on 2026-10-24 with all 13 tracked tasks complete. Its seven requirements are synced into main specs. PlantSim exposes the seven typed tags, manual and seeded random faults, a production Docker image and a Bridge configuration profile. All 182 Release tests passed locally; CI build/test, strict OpenSpec, M4 SIGKILL and M5 edge-outage jobs passed. Container end-to-end verification stored all seven tags and delivered ordered recovery/gap events within the unchanged NFR-04 deadline. UaExpert checks confirmed the tags and all five fault methods. See the [review package](docs/m6-review.md), [archived change](openspec/changes/archive/2026-10-24-add-plant-simulator/) and [PR #3](https://github.com/quenzvezda/Telemetrix/pull/3).

M7 was accepted by the reviewer through [PR #4](https://github.com/quenzvezda/Telemetrix/pull/4) and archived on 2026-10-25 with all 13 tracked tasks complete. Its six requirements are synced into main specs. MqttSim and PlantSim share the pumping-line model; Bridge selects MQTT or OPC UA by configuration, preserving the Transformer and database schema. All 208 tests passed across the 207-test regression suite and the full 600-second protocol comparison. Both protocols stored exactly 600 measurements per tag (4,200 each) over the same source-time window. Production-container verification covered seven tags and ordered recovery events after a broker restart. CI build/test, strict OpenSpec, M4 SIGKILL and M5 edge-outage jobs passed. See the [review package](docs/m7-review.md) and [archived change](openspec/changes/archive/2026-10-25-add-mqtt-source/).

M8 started on 2026-10-25. The reviewer approved 500 samples / 5 seconds per asset, whichever comes first, as the baseline and deferred final D5 selection until the load-test comparison. The reviewer subsequently authorized implementation across all task groups and requested a PR for review before archival. After measurement, the reviewer explicitly accepted the same thresholds as the final D5 choice on 2026-10-25 (ADR-0015).

M8 was accepted for closure and archived on 2026-10-25 after the reviewer approved [PR #5](https://github.com/quenzvezda/Telemetrix/pull/5) without issues. The reviewer explicitly deferred manual testing until M9 is complete: archived tasks 4.2 (existing M7 volume) and 6.5 (manual dashboard acceptance) remain unchecked, carried into [M9 tasks 5.1 and 5.2](openspec/changes/add-opcua-security/tasks.md). The archive therefore records 20/22 verified tasks with an approved deferral, not 22 passing checks. Release build has zero warnings/errors; the regression suite passes 221 tests with one explicit load profile excluded. All four full-duration load runs pass on the recorded workstation. The accepted 500/5 s thresholds have default-rate p95 7,774.686 ms and throughput 5,250.343 samples/s with complete drain and zero dead letters. Production M4 SIGKILL and short M5 outage regressions pass. Dashboard and observability requirements are synced into main specs, alongside the telemetry-storage additions. See the [archive](openspec/changes/archive/2026-10-25-add-observability/), [review procedure](docs/m8-review.md), [load evidence](docs/load-test.md) and [accepted D5 ADR](docs/adr/0015-retain-measured-batch-thresholds.md). M9 implementation has not started; its final acceptance must include the deferred checks.

M9 started later on 2026-10-25 after the reviewer authorized implementation across all groups and requested a PR before archival. Security implementation, explicit certificate trust, secret scanning, fresh-clone verification and the secured dashboard check are implemented. Agent verification on the existing M7 volume preserved all 121,100 paused measurements across two migration runs; the secured source outage check recorded gaps and restored all seven tags. The review procedure and evidence are in [docs/m9-review.md](docs/m9-review.md). Final acceptance of tasks 5.1 and 5.2 remains with the reviewer, and OpenSpec must stay active until approval.

NFR-13 (tests) is not a milestone: every change lands its own unit and integration tests, and CI (`.github/workflows/ci.yml`) stays green.

## Open decisions

No open decisions remain. D5 was resolved after the approved M8 measurement checkpoint; final outcomes are recorded in `docs/adr/` and the affected planning artifacts.

## Resolved decisions

- D5 (needed by M8): retain 500 samples / 5 seconds per asset, whichever comes first. The reviewer explicitly accepted this choice on 2026-10-25 after the baseline/alternative comparison. See [ADR-0015](docs/adr/0015-retain-measured-batch-thresholds.md) and [measured evidence](docs/load-test.md). Existing defaults remain unchanged.

- D3 (needed by M5): configurable buffer sized for at least one hour at default load; pause collection on overflow, preserve accepted batches and report the collection gap. Accepted in [ADR-0013](docs/adr/0013-bound-edge-buffer-and-pause-collection.md). The reviewer authorized autonomous implementation across all M5 task groups on 2026-10-22.

- D4 (needed by M4): defer `plant.telemetry.clean` (FR-25) to the separate-sink stretch goal. Accepted in [ADR-0012](docs/adr/0012-defer-clean-topic-to-separate-sink.md).

- D1 (needed by M3): `dead-letter` is the unknown-tag default; `create` is an explicit opt-in. Accepted in [ADR-0011](docs/adr/0011-dead-letter-unknown-tags-by-default.md).
- D2 (needed by M2/M3): numeric sample values, booleans as 1/0 from the source adapter, strings out of scope for v1. Accepted in [ADR-0010](docs/adr/0010-numeric-sample-values-in-v1.md).

## Stretch goals (after M9)

Schema registry (Avro/Protobuf), separate DB sink, stream aggregation and `plant.alarms`, OPC UA deadband (report by exception), Sparkplug B, operator write-back via OPC UA method call, three-broker Kafka cluster. Details in the design doc.
