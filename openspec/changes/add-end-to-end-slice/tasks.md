# Tasks

## 1. Decisions and setup

- [x] 1.1 Record the accepted D1 (`dead-letter` default, `create` opt-in) in ADR-0011, confirm D2 from ADR-0010 (nullable numeric values, booleans as 1/0, strings out of scope for v1), and align ROADMAP.md and the M3 planning artifacts with both decisions; verify strict OpenSpec validation
- [x] 1.2 Add `Confluent.Kafka`, `Npgsql`, `Dapper` to `Directory.Packages.props` and reference them from the bridge/transformer; verify `dotnet build`

Group 1 implementation and required verification are complete. Reviewer accepted the code review, Release build, resolved dependencies and all 35 tests on 2026-10-18 and authorized group 2; see [the review package](../../../docs/m3-group1-review.md).

## 2. Contract

- [x] 2.1 Add `TelemetryBatch`, `BatchSample` and `TelemetryJson` (source-generated context) to `Telemetrix.Contracts`; verify a round-trip unit test against the sample JSON in `docs/plant-telemetry-pipeline.md`, including UTC `Z` timestamps and booleans as 1/0

Group 2 implementation and required verification are complete. Reviewer accepted the code review, Release build, 44 Bridge tests, all 46 regression tests and strict OpenSpec validation on 2026-10-18 and authorized group 3; see [the review package](../../../docs/m3-group2-review.md).

## 3. Bridge: batching and publishing

- [x] 3.1 Implement `AssetBatcher` (per-asset batches, size/age flush, flush on shutdown, no empty batches); verify unit tests for each scenario in `specs/telemetry-batching/spec.md` using `FakeTimeProvider`
- [x] 3.2 Implement `TopicInitializer` for `plant.telemetry.raw` (6 partitions, 7 d); verify an integration test against a Testcontainers Kafka shows the topic and its config after start, and a second start leaves it unchanged
- [x] 3.3 Implement `KafkaBatchPublisher` (key = asset id, JSON value, `acks=all`, idempotence on) and wire channel → batcher → publisher in the bridge host; verify an integration test that consumes the produced message and asserts key, partition stability and contract shape
- [x] 3.4 Run the bridge against OPC PLC; verify in Kafka UI that `plant.telemetry.raw` receives one message per asset every ~5 s

Group 3 implementation and verification are complete. Reviewer accepted the code and manual verification on 2026-10-18 and authorized group 4 after confirming a single Bridge instance with consecutive offsets and final batches on shutdown; see [the review package](../../../docs/m3-group3-review.md).

## 4. Transformer: pipeline

- [x] 4.1 Implement `QualityMapper` and `RangeCheck`; verify unit tests for Good/Uncertain/Bad codes, out-of-range, missing value, tag without range
- [x] 4.2 Implement `TagResolver` with the unknown-tag policy (`dead-letter` default, `create`); verify unit tests for both policies; the transformer creates `plant.telemetry.dlq` (1 partition, 30 d) at startup
- [x] 4.3 Implement `RawBatchConsumer` in group `transformer`; verify an integration test that two consumers split the 6 partitions

Group 4 implementation and verification are complete. Reviewer accepted the results and authorized group 5 on 2026-10-18 after confirming the Release build, 29 Transformer unit cases, both consumer cases, catalog/DLQ cases and local Transformer startup/shutdown; see [the review package](../../../docs/m3-group4-review.md).

## 5. Transformer: storage

- [x] 5.1 Implement `MeasurementWriter` (temp staging table, binary COPY, `INSERT … ON CONFLICT DO NOTHING`, one transaction per batch); verify Testcontainers TimescaleDB tests: a batch inserts N rows, a replay inserts 0, a forced mid-write failure leaves 0 rows
- [ ] 5.2 Wire consumer → pipeline → writer; verify an end-to-end integration test (Kafka + TimescaleDB containers) from a produced batch to rows with correct quality and batch id

## 6. Milestone check

- [ ] 6.0 Remove the `--ignore-exit-code 8` line from `tests/Directory.Build.props` now that every test project has tests; verify `dotnet test` exits 0

- [ ] 6.1 Done when: with the compose stack, bridge and transformer running, rows appear in `measurement` and `SELECT time, value FROM measurement JOIN tag USING (tag_id) WHERE name = 'MotorTemperature' ORDER BY time DESC LIMIT 20` shows the live trend
