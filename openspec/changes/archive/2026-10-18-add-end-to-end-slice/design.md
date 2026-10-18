# Design

## Context

M2 left the bridge writing samples into a `Channel<Sample>` that only a logger reads. M3 adds every remaining stage once, in its simplest correct form. See proposal.md for scope; ADR-0003, -0004, -0007 and -0008 fix the shape.

## Goals / Non-Goals

**Goals:** rows in `measurement` from live OPC PLC data; an idempotent write path from day one, because retrofitting idempotence is harder than building it.

**Non-Goals:** commit-after-write and schema dead-lettering (M4); disk buffering when Kafka is down (M5); metrics (M8).

## Decisions

### Bridge

- **Batcher** (`Batching/AssetBatcher`): one reader of the channel keeps a `Dictionary<assetId, OpenBatch>`. Size flush happens inline on append; age flush is a `PeriodicTimer` tick (1 s) that flushes batches older than the threshold. Flushed batches go to a bounded `Channel<TelemetryBatch>` so a slow producer back-pressures the batcher rather than growing memory. Rejected: one timer per asset — more moving parts for no gain at this scale.
- **Batch id**: `Guid.CreateVersion7()` — unique and time-ordered, which helps when reading the DLQ and logs.
- **Producer** (`Publishing/KafkaBatchPublisher`): Confluent.Kafka `IProducer<string, byte[]>`, key = asset id, value = UTF-8 JSON from the shared serializer options. Producer reliability settings (`acks=all`, idempotence) are formalised in M4 but set from the start.
- **Topic bootstrap** (`Publishing/TopicInitializer`): `IHostedService` that runs before the workers and calls `AdminClient.CreateTopicsAsync`, treating `TopicAlreadyExists` as success. The bridge owns `plant.telemetry.raw` (and later `plant.events`); the transformer owns `plant.telemetry.dlq` and `plant.telemetry.clean`.

### Contracts

- `TelemetryBatch` and `BatchSample` records plus a `TelemetryJson` class exposing `JsonSerializerOptions` (camelCase, UTC `DateTimeOffset`) backed by a source-generated `JsonSerializerContext`. Both services serialise only through it. Values remain `double?` end to end under ADR-0010; source adapters convert booleans to 1/0 before emitting samples, and strings are out of scope for v1.

### Transformer

- **Consumer** (`Consuming/RawBatchConsumer`): `BackgroundService` running a blocking `Consume` loop on a dedicated thread (`Task.Run` with `LongRunning`), one batch at a time. Client defaults for commits in M3.
- **Pipeline** (`Pipeline/`): pure functions, unit-testable without Kafka or Postgres — `QualityMapper` (severity bits 30–31 of the status code), `RangeCheck`, `TagResolver` (in-memory cache of `(asset, tag) → tag_id` loaded at startup, refreshed on miss).
- **Storage** (`Storage/MeasurementWriter`): per batch, one transaction: `CREATE TEMP TABLE measurement_stage (LIKE measurement INCLUDING DEFAULTS) ON COMMIT DROP`, Npgsql binary `COPY` into it, then `INSERT INTO measurement SELECT … FROM measurement_stage ON CONFLICT (tag_id, time) DO NOTHING`. Rejected: multi-row `INSERT … VALUES` — slower and parameter-count limited.
- **Unknown-tag policy** follows accepted D1 (ADR-0011): `dead-letter` by default sends only unknown-tag samples to `plant.telemetry.dlq` with the original batch id and reason `unknown-tag`, while known-tag samples are stored. Explicit `create` configuration inserts unknown tags without unit or valid range before storing their samples.

## Risks / Trade-offs

- [Auto-commit can commit an offset before its batch is written, losing it on crash] → accepted for M3 only; M4 switches to commit-after-write and proves it with NFR-03.
- [Two samples for one tag with the same source timestamp but different values] → the second is dropped by design (ADR-0003); log a counter when `INSERT` affects fewer rows than staged so it is visible.
- [Tag cache stale after `create` policy inserts] → resolver inserts with `ON CONFLICT (asset_id, name) DO NOTHING RETURNING`, then re-selects, and updates the cache.
