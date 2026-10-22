# Design

## Context

M4 is accepted and archived. The bridge currently publishes sequentially with an
idempotent producer and has SDK-assisted OPC UA recovery. ADR-0005 selects SQLite;
ADR-0013 resolves D3. See proposal.md for motivation.

## Goals / Non-Goals

**Goals:** FIFO delivery through outages and restarts; bounded payload storage;
source and capacity gaps; 30-minute outage and 30-second recovery acceptance.

**Non-Goals:** HistoryRead, infinite retention or cross-process exactly-once
Kafka delivery. Database identity remains ADR-0003.

## Decisions

### Single admission path and independent replay

An admission gate serializes batches and plant events. With an empty buffer,
send directly; after failed acknowledgement, append the same bytes to SQLite.
While any entry exists, append every new entry behind it. Independent replay
reads oldest entries and deletes only acknowledged entries. Direct delivery and
transition back from buffering share the admission gate, preventing overtaking.
Replay never waits for admission to a full buffer.

Keep the existing ten-second delivery timeout and inject the transport for tests.
Startup resumes persisted entries. Graceful shutdown persists accepted input;
it does not wait for Kafka to recover.

### SQLite and capacity

Use WAL and synchronous=FULL. Table `buffered_entries` stores sequence, topic,
asset key, payload, sample count and UTC creation time. Retain exact payloads.
Cache counters after startup and successful operations; storage locks never span
network waits. Each bridge instance owns its database file.

Default `Buffer:MaxBytes` is 64 MiB. Test an hour of seven tags at the default
source rate and five-second batches. Pause at 80%, resume at 50%; a hard-quota
append waits while replay frees capacity. No expiry. SQLite overhead and WAL
are additional disk usage. Bounded channels retain accepted samples while waiting.
Pause/restart stops and recreates the subscription; report `buffer-capacity` gaps.

### Recovery and events

Keep `SessionReconnectHandler` for transfer/recreate recovery with bounded retry.
Session creation failures use exponential delay (1 s, factor 2, 15 s cap, 20%
jitter). Keep-alive checks run every five seconds. Initial unavailability retries.

A per-source tracker serializes transitions and remembers each asset's last
source timestamp. Emit lost once, restored on recovery, and Gap on the first
fresh sample per asset. Initial connection is not an interruption. All events
use UTC and the same publication path as batches. Contracts owns PlantEvent and
JSON settings. Bridge owns `plant.events` (3 partitions, 30-day retention),
leaving existing topics unchanged. Topic startup retries independently while
Kafka is unavailable so local collection and persisted replay can start.

### Observability

Meter `Telemetrix.Edge.Bridge` exposes `bridge.buffer.batches` and
`bridge.buffer.samples` from cached counters. Log buffering, empty, capacity
pause/resume and retry transitions. M8 supplies exporters.

## Risks / Trade-offs

- Ambiguous acknowledgements may replay; Transformer deduplicates by tag/time.
- Spill-on-failure retains an in-memory window; restart tests cover persisted
  entries, not arbitrary crashes before spill.
- Source changes during capacity pause require history to recover; report gaps.
- Drain must exceed live throughput; measure after a real 30-minute outage in a
  disposable acceptance stack.

## Migration Plan

Create SQLite automatically in a writable persistent directory. Never share a
file between bridge instances. Preserve nonempty files on rollback for later
replay. No TimescaleDB migration is required.
