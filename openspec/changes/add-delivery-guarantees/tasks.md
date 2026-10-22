# Tasks

## 1. Producer durability (bridge)

- [x] 1.1 Set `Acks=All`, `EnableIdempotence=true`, `MaxInFlight=5` in `Publishing` producer config; verify with a unit test in `Telemetrix.Edge.Bridge.Tests` asserting the built config (NFR-08)

## 2. Manual commits (transformer)

- [x] 2.1 Disable auto-commit and auto offset store; consume in group `transformer`; verify with a unit test on the consumer config (FR-20)
- [x] 2.2 Restructure the consume loop to `validate → map → write → commit`, committing only after `Storage` returns; verify with a unit test using a fake storage that throws, asserting no commit happened (FR-24)
- [x] 2.3 Wrap the write in an unbounded exponential-backoff retry via Microsoft.Extensions.Resilience; verify with a unit test that a storage failing twice then succeeding results in exactly one commit
- [x] 2.4 Handle partitions revoked/lost as in design.md; verify by an integration test that starts a second consumer in the group mid-stream and asserts the row count is unchanged

## 3. Dead-letter topic

- [x] 3.1 Create `plant.telemetry.dlq` (1 partition, 30 d retention) at transformer startup via AdminClient; verify the topic appears in Kafka UI with those settings
- [ ] 3.2 Validate envelope and `schemaVersion`; produce failures to the DLQ with the `dlq.*` headers from design.md, then commit; verify with unit tests for `invalid-json`, `schema-violation`, `unsupported-schema-version` (FR-21)
- [ ] 3.3 Integration test: produce a malformed message between two valid batches; verify it lands in the DLQ with headers and both valid batches are stored

## 4. Ordering

- [ ] 4.1 Unit test: two consecutive batches of one asset are written in order and source timestamps per tag are non-decreasing (NFR-05)

## 5. Delivery guarantees proven

- [ ] 5.1 `ExactlyOnceTests` in `Telemetrix.Integration.Tests` (Kafka + TimescaleDB containers), including a deliberately replayed batch; verify `dotnet test` passes and row count equals samples sent (NFR-01)
- [ ] 5.2 `KillMidBatchTests`: crash after DB commit, before Kafka commit; restart; verify no gaps and no duplicates (NFR-03)
- [ ] 5.3 Manual check from the ROADMAP "Done when": run the stack under load, `docker kill` the transformer mid-stream, restart it, and verify `SELECT count(*) FROM measurement` equals samples sent by the simulator; record the result in the change's notes
- [ ] 5.4 Update ROADMAP.md M4 status; verify CI is green
