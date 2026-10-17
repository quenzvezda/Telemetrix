# Proposal

## Why

M3 proves data can flow end to end, but it does not prove the central guarantee: no sample lost and no duplicate row when a service dies mid-batch. Until offsets are committed only after a durable write, malformed batches are set aside instead of blocking the stream, and the producer cannot introduce duplicates or reordering, any crash can silently lose or double data.

Milestone: M4 — Delivery guarantees.

## What Changes

- Transformer consumes `plant.telemetry.raw` in consumer group `transformer` with auto-commit disabled (FR-20).
- Transformer commits a batch's offset only after its measurements are durably written; a failed write is retried and never committed (FR-24, NFR-03).
- Batches that fail schema validation go to `plant.telemetry.dlq` under their original key, with the error reason in a header, and processing continues (FR-21).
- Samples of one asset are processed in source-timestamp order (NFR-05).
- Bridge producer runs with `acks=all` and idempotence enabled (NFR-08).
- Integration tests prove exactly-once storage (NFR-01) and kill-mid-batch recovery (NFR-03).
- Out of scope: republishing to `plant.telemetry.clean` (FR-25, open decision D4).

## Capabilities

### New Capabilities

- `telemetry-transform`: consumption, validation, dead-lettering and commit semantics of the transformer (adds to the requirements introduced in M3).
- `telemetry-publishing`: producer durability settings for raw batches (adds to the requirements introduced in M3).

### Modified Capabilities

None (M3 is not archived yet; requirements are added as new).

## Impact

- `Telemetrix.Transformer` (Consuming, Pipeline), `Telemetrix.Edge.Bridge` (Publishing), `Telemetrix.Contracts` (validation of `schemaVersion`).
- New topic `plant.telemetry.dlq` (1 partition, 30 days), created by the transformer at startup (ADR-0007).
- `tests/Telemetrix.Transformer.Tests`, `tests/Telemetrix.Integration.Tests` (Kafka + TimescaleDB via Testcontainers).
