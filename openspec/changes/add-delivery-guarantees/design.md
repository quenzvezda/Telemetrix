# Design

## Context

After M3 the transformer consumes `plant.telemetry.raw`, maps quality, resolves tags and writes through a staging table with `INSERT … ON CONFLICT DO NOTHING` (ADR-0003). This change makes the commit discipline explicit and proves it. See proposal.md for motivation.

## Goals / Non-Goals

**Goals:**
- A single, obvious place where "write, then commit" happens.
- Reproducible proof of NFR-01 and NFR-03 in `dotnet test`.

**Non-Goals:**
- Kafka transactions / exactly-once semantics on the Kafka side (rejected in ADR-0003).
- `plant.telemetry.clean` republishing (FR-25, decision D4).
- Unknown-tag handling policy (decided in M3, D1).

## Decisions

### Consume loop: one batch at a time per partition, commit after write

`Consuming/TransformerConsumer` (a `BackgroundService`) runs `Consume → Validate → Map → Write → Commit` sequentially. Confluent.Kafka settings: `EnableAutoCommit=false`, `EnableAutoOffsetStore=false`, `AutoOffsetReset=Earliest`, `IsolationLevel=ReadCommitted` is not needed. After `Storage` returns, the loop calls `StoreOffset(result)` then `Commit(result)` synchronously.

- Alternative: batch several Kafka messages per DB transaction and commit the highest offset. Faster, but complicates partial failure; revisit only if NFR-07 (M8) needs it. The `ON CONFLICT` key makes it safe to add later.
- A DB write failure is retried with `Microsoft.Extensions.Resilience` (exponential backoff, unbounded attempts, jitter); the loop does not advance. This intentionally stalls the partition rather than skip data.

### Dead-letter topic

Validation failures are deterministic, so they are never retried. The original key and value bytes are produced to `plant.telemetry.dlq` (1 partition, 30 days retention; created by the transformer via AdminClient, ADR-0007) with headers:

| Header | Value |
| --- | --- |
| `dlq.reason` | short machine code: `invalid-json`, `schema-violation`, `unsupported-schema-version` |
| `dlq.error` | human-readable message (truncated to 1 KB) |
| `dlq.source.topic` / `dlq.source.partition` / `dlq.source.offset` | origin of the message |
| `dlq.timestamp` | UTC ISO-8601 time of dead-lettering |

The DLQ produce is awaited (acks=all) before the offset commit, so a crash in between replays into the DLQ again — duplicates in the DLQ are acceptable.

### Rebalance handling

`SetPartitionsRevokedHandler` waits for the in-flight batch to finish (write + commit) before returning; because commit is synchronous inside the loop, no extra bookkeeping is needed. On `SetPartitionsLostHandler` nothing is committed — the new owner replays, and `ON CONFLICT` absorbs the duplicate.

### Ordering

Ordering comes from keying by `assetId` (ADR-0004) plus single-threaded processing per consumer. The pipeline must not reorder samples within a batch; storage sorts nothing. A unit test asserts monotonic source timestamps per tag across consecutive batches of one asset.

### Producer settings (bridge)

`Publishing/BatchProducer`: `Acks=All`, `EnableIdempotence=true`, `MaxInFlight=5` (the maximum that keeps ordering with idempotence), `LingerMs=5`, `CompressionType=Lz4`. Asserted by a unit test on the built `ProducerConfig`.

### Verification

- **NFR-01** — `Integration.Tests/ExactlyOnceTests`: Testcontainers Kafka + TimescaleDB (`timescale/timescaledb:2.30.2-pg17` with `deploy/sql/init.sql`); produce N known batches, deliver one of them twice, run the transformer host in-process, assert `count(*) == N samples`.
- **NFR-03** — `Integration.Tests/KillMidBatchTests`: inject a fault hook in `Storage` that throws `OperationCanceledException` *after* the DB transaction commits but before the Kafka commit; stop the host abruptly, start a new host in the same group, assert no gaps and no duplicates. A manual check (`docker kill transformer` under load) is documented in tasks.

## Risks / Trade-offs

- [Poison batch that passes validation but always fails the write (e.g. FK violation) stalls a partition forever] → log at Error with batchId, expose consumer lag (M8); unknown tags are handled before the write per D1.
- [Synchronous commit per batch limits throughput] → measured in M8 (NFR-07); multi-message transactions are the fallback.
- [DLQ duplicates after crash] → acceptable; DLQ is for human inspection.
