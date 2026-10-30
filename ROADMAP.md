# Roadmap

Nine milestones plus post-M9 backlog milestones, each ending in something you can see working. A thin path runs end to end by M3; M4–M9 harden it. Build the client side before the simulator or dashboards.

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
| M8 | Observability and load | `add-observability` | FR-31, FR-32, FR-40, FR-41, NFR-06, NFR-07, NFR-12 | Dashboard shows trends, pump state and gaps; p95 latency < 10 s; 5,000 samples/s without growing lag | Done |
| M9 | Security | `add-opcua-security` | NFR-09, NFR-10 | An untrusted client certificate is rejected; no secret in the repo | Done |
| M10 | Python transformations ([issue #7](https://github.com/quenzvezda/Telemetrix/issues/7)) | `add-python-transformations` | FR-21, FR-24, FR-26, FR-27, FR-28, NFR-01, NFR-03, NFR-05, NFR-06, NFR-07, NFR-12 | Two Asset Batches select two registered Python scripts by label; raw Measurements stay intact, derived Measurements and every failure are recorded, and replay after a script change stores nothing twice | In progress |

M3 was accepted and archived on 2026-10-18 with all 14 tasks complete. Its four capabilities are in main specs; see the [archived change](openspec/changes/archive/2026-10-18-add-end-to-end-slice/) and [review evidence](docs/m3-group6-review.md). The observed stopped-consumer backlog remains documented; zero lag and M4 delivery guarantees are not claimed.

M4 was accepted by the reviewer and archived on 2026-10-22 with all 13 tracked tasks complete. Release CI passed all 137 tests and strict OpenSpec validation; the production Transformer SIGKILL check stored all 20,000 samples with zero lag. Its requirements are in main specs; see the [archived change](openspec/changes/archive/2026-10-22-add-delivery-guarantees/) and [review package](docs/m4-review.md).

M5 was accepted by the reviewer and merged through [PR #2](https://github.com/quenzvezda/Telemetrix/pull/2). Its OpenSpec change was archived on 2026-10-24 with all 15 tracked tasks complete and its requirements synced into main specs. D3 is resolved in ADR-0013. Release CI passed all 164 tests, strict OpenSpec validation, the M4 SIGKILL regression and the short M5 outage check. The full Kafka outage lasted 1,801.140 measured seconds and drained all 12,880 samples with an empty buffer and zero lag. Production OPC UA-to-Kafka recovery restored all seven fresh tags and ordered plant events within 30 seconds. See the [review package](docs/m5-review.md) and [archived change](openspec/changes/archive/2026-10-24-add-edge-store-and-forward/).

M6 was accepted by the reviewer and archived on 2026-10-24 with all 13 tracked tasks complete. Its seven requirements are synced into main specs. PlantSim exposes the seven typed tags, manual and seeded random faults, a production Docker image and a Bridge configuration profile. All 182 Release tests passed locally; CI build/test, strict OpenSpec, M4 SIGKILL and M5 edge-outage jobs passed. Container end-to-end verification stored all seven tags and delivered ordered recovery/gap events within the unchanged NFR-04 deadline. UaExpert checks confirmed the tags and all five fault methods. See the [review package](docs/m6-review.md), [archived change](openspec/changes/archive/2026-10-24-add-plant-simulator/) and [PR #3](https://github.com/quenzvezda/Telemetrix/pull/3).

M7 was accepted by the reviewer through [PR #4](https://github.com/quenzvezda/Telemetrix/pull/4) and archived on 2026-10-25 with all 13 tracked tasks complete. Its six requirements are synced into main specs. MqttSim and PlantSim share the pumping-line model; Bridge selects MQTT or OPC UA by configuration, preserving the Transformer and database schema. All 208 tests passed across the 207-test regression suite and the full 600-second protocol comparison. Both protocols stored exactly 600 measurements per tag (4,200 each) over the same source-time window. Production-container verification covered seven tags and ordered recovery events after a broker restart. CI build/test, strict OpenSpec, M4 SIGKILL and M5 edge-outage jobs passed. See the [review package](docs/m7-review.md) and [archived change](openspec/changes/archive/2026-10-25-add-mqtt-source/).

M8 started on 2026-10-25. The reviewer approved 500 samples / 5 seconds per asset, whichever comes first, as the baseline and deferred final D5 selection until the load-test comparison. The reviewer subsequently authorized implementation across all task groups and requested a PR for review before archival. After measurement, the reviewer explicitly accepted the same thresholds as the final D5 choice on 2026-10-25 (ADR-0015).

M8 was accepted for closure and archived on 2026-10-25 after the reviewer approved [PR #5](https://github.com/quenzvezda/Telemetrix/pull/5) without issues. The reviewer explicitly deferred manual testing until M9: archived tasks 4.2 (existing M7 volume) and 6.5 (manual dashboard acceptance) remain unchecked in that historical snapshot and were carried into [M9 tasks 5.1 and 5.2](openspec/changes/archive/2026-10-25-add-opcua-security/tasks.md). The M8 archive therefore records 20/22 verified tasks with an approved deferral, not 22 passing checks; both deferred checks were subsequently completed and accepted in M9. Release build has zero warnings/errors; the M8 regression suite passes 221 tests with one explicit load profile excluded. All four full-duration load runs pass on the recorded workstation. The accepted 500/5 s thresholds have default-rate p95 7,774.686 ms and throughput 5,250.343 samples/s with complete drain and zero dead letters. Production M4 SIGKILL and short M5 outage regressions pass. Dashboard and observability requirements are synced into main specs, alongside the telemetry-storage additions. See the [archive](openspec/changes/archive/2026-10-25-add-observability/), [review procedure](docs/m8-review.md), [load evidence](docs/load-test.md) and [accepted D5 ADR](docs/adr/0015-retain-measured-batch-thresholds.md).

M9 was accepted by the reviewer on 2026-10-25 through [PR #6](https://github.com/quenzvezda/Telemetrix/pull/6) and archived with all 13 tasks complete. Its four connection-security requirements are synced into main specs. The reviewer completed the deferred M8 checks: two migration runs preserved all 173,369 paused measurements, and the secured dashboard showed live trends, pump trip/recovery and a 73-second source gap for both assets. A historical display regression was fixed and visually verified: missing days no longer connect trends or carry forward pump state. The final focused dashboard suite passes all five tests; Release build has zero warnings/errors. The earlier full local regression passed 236 tests with one explicit long load profile excluded; final remote CI is recorded on PR #6. D5 remains 500 samples / five seconds per asset. See the [review evidence](docs/m9-review.md) and [archived change](openspec/changes/archive/2026-10-25-add-opcua-security/). All nine milestones and the carried-forward manual checks are complete.

M10 started on 2026-10-31 from backlog [issue #7](https://github.com/quenzvezda/Telemetrix/issues/7). The reviewer asked for implementation to completion using the agent's recommendation for each open design decision; those recommendations are recorded for review in [ADR-0016](docs/adr/0016-run-python-transformations-in-a-managed-child-process.md), [ADR-0017](docs/adr/0017-select-transformations-by-a-label-in-batch-schema-v2.md) and [ADR-0018](docs/adr/0018-preserve-raw-measurements-and-store-derived-tags.md). Scripting is disabled by default; final acceptance and archival remain the reviewer's.

NFR-13 (tests) is not a milestone: every change lands its own unit and integration tests, and CI (`.github/workflows/ci.yml`) stays green.

## Open decisions

No open decisions remain. D5 was resolved after the approved M8 measurement checkpoint; final outcomes are recorded in `docs/adr/` and the affected planning artifacts.

## Resolved decisions

- M10 design (issue #7): managed Python child process (ADR-0016), label in Batch schema version 2 assigned per Asset by the Bridge (ADR-0017), raw Measurements preserved with derived Tags and a durable per-Batch run record (ADR-0018). Recommended by the agent and adopted on 2026-10-31 at the reviewer's request, pending review in the M10 PR.

- D5 (needed by M8): retain 500 samples / 5 seconds per asset, whichever comes first. The reviewer explicitly accepted this choice on 2026-10-25 after the baseline/alternative comparison. See [ADR-0015](docs/adr/0015-retain-measured-batch-thresholds.md) and [measured evidence](docs/load-test.md). Existing defaults remain unchanged.

- D3 (needed by M5): configurable buffer sized for at least one hour at default load; pause collection on overflow, preserve accepted batches and report the collection gap. Accepted in [ADR-0013](docs/adr/0013-bound-edge-buffer-and-pause-collection.md). The reviewer authorized autonomous implementation across all M5 task groups on 2026-10-22.

- D4 (needed by M4): defer `plant.telemetry.clean` (FR-25) to the separate-sink stretch goal. Accepted in [ADR-0012](docs/adr/0012-defer-clean-topic-to-separate-sink.md).

- D1 (needed by M3): `dead-letter` is the unknown-tag default; `create` is an explicit opt-in. Accepted in [ADR-0011](docs/adr/0011-dead-letter-unknown-tags-by-default.md).
- D2 (needed by M2/M3): numeric sample values, booleans as 1/0 from the source adapter, strings out of scope for v1. Accepted in [ADR-0010](docs/adr/0010-numeric-sample-values-in-v1.md).

## Stretch goals (after M9)

Schema registry (Avro/Protobuf), separate DB sink, stream aggregation and `plant.alarms`, OPC UA deadband (report by exception), Sparkplug B, operator write-back via OPC UA method call, three-broker Kafka cluster. Details in the design doc.
