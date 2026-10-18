# Proposal

## Why

A thin path from OPC UA to a database row, working end to end, exposes integration problems while they are still cheap and gives every later milestone something real to harden. Nothing after M3 adds a new stage — only guarantees, resilience and inputs.

## What Changes

Milestone: M3 — First end-to-end slice.

- Bridge groups samples into one batch per asset, flushed at 500 samples or 5 s, whichever first (FR-13, ADR-0004).
- Bridge produces each batch as a versioned JSON message to `plant.telemetry.raw`, keyed by asset id, with a unique batch id; it creates the topic at startup if missing (FR-14, ADR-0007, ADR-0008).
- Transformer consumes `plant.telemetry.raw` in consumer group `transformer`, maps status codes to Good / Uncertain / Bad, marks out-of-range values Bad (FR-22), and resolves asset/tag names to database ids with `dead-letter` as the default unknown-tag policy and `create` as an explicit opt-in (FR-23, ADR-0011).
- Transformer writes samples in bulk; a replayed batch inserts nothing twice (FR-30, ADR-0003).
- Offset handling stays at client defaults here; commit-after-write, schema dead-lettering and the delivery tests are M4.

## Capabilities

### New Capabilities

- `telemetry-batching`: how the bridge groups samples into per-asset batches.
- `telemetry-publishing`: the raw topic, message key and batch message format the bridge produces.
- `telemetry-transform`: how the transformer consumes, judges and resolves samples.
- `telemetry-storage`: what a stored measurement is and how duplicates are treated.

### Modified Capabilities

None.

## Impact

- `src/Telemetrix.Contracts` (batch record, JSON options), `src/Telemetrix.Edge.Bridge/Batching`, `/Publishing`, `src/Telemetrix.Transformer/Consuming`, `/Pipeline`, `/Storage`.
- New packages: `Confluent.Kafka`, `Npgsql`, `Dapper`.
- Tests in `tests/Telemetrix.Edge.Bridge.Tests`, `tests/Telemetrix.Transformer.Tests`, `tests/Telemetrix.Integration.Tests` (Testcontainers Kafka + TimescaleDB).
- D1 is accepted in ADR-0011 (`dead-letter` default, `create` opt-in); D2 is already accepted in ADR-0010 (nullable numeric values, booleans converted to 1/0 by source adapters, strings out of scope for v1).
