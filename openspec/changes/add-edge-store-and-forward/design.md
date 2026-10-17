# Design

## Context

After M3/M4 the bridge produces each batch straight to Kafka with an idempotent `acks=all` producer and has only basic OPC UA reconnect from M2. See proposal.md for motivation. ADR-0005 already chose SQLite as the buffer; this design fixes how it is used.

## Goals / Non-Goals

**Goals:**
- One FIFO path for batches and plant events that keeps per-asset order across outages and restarts.
- No disk I/O on the happy path.
- Reconnect that meets NFR-04 (< 30 s) with the OPC Foundation client.

**Non-Goals:**
- Buffering inside the transformer (Kafka already is its buffer).
- Back-filling gaps from OPC UA history (HistoryRead) — gaps are reported, not filled.

## Decisions

### Spill-on-failure with a single FIFO (Buffer/StoreAndForwardQueue)

The publisher has two modes:

- **Direct** — buffer is empty: produce to Kafka; on a failed delivery report (after librdkafka's `message.timeout.ms`, default set to 30 s) append the batch to SQLite and switch to **Draining**.
- **Draining** — buffer is not empty: every new batch or event is appended to SQLite, never produced directly. A drain loop reads the oldest rows (`ORDER BY seq LIMIT 100`), produces them one partition-ordered sequence at a time, and deletes each row after its delivery report succeeds. When the table is empty, switch back to Direct.

This keeps one global FIFO, so per-asset order (NFR-05) holds without per-asset bookkeeping. At startup the bridge starts in Draining if the table has rows (restart survival).

- Alternative: always write every batch to SQLite first (transactional outbox). Simpler state machine, but a disk write per batch on the happy path; kept as the fallback if the mode switch proves fragile.
- With idempotence enabled, librdkafka fails later in-flight messages of a partition after an earlier one fails, so delivery-report failures arrive in order and are appended in order.

SQLite schema (WAL mode, `synchronous=FULL`):

```sql
CREATE TABLE outbox (
  seq          INTEGER PRIMARY KEY AUTOINCREMENT,
  topic        TEXT    NOT NULL,   -- plant.telemetry.raw | plant.events
  msg_key      TEXT    NOT NULL,   -- assetId
  payload      BLOB    NOT NULL,   -- serialized JSON, byte-identical on replay
  sample_count INTEGER NOT NULL,
  created_at   TEXT    NOT NULL    -- UTC ISO-8601
);
```

A crash after a successful produce but before the row delete replays that batch once more; the transformer absorbs it via `ON CONFLICT` (ADR-0003).

### Metrics

`System.Diagnostics.Metrics` meter `Telemetrix.Edge.Bridge`: observable gauges `bridge.buffer.batches` and `bridge.buffer.samples` (read from cached counters, not a `COUNT(*)` per scrape). Exported via OpenTelemetry in M8.

### Reconnect (Sources/OpcUaSourceAdapter)

Use the SDK's `SessionReconnectHandler` on `KeepAlive` failure (keep-alive interval 5 s). If reconnect fails because the server restarted (`BadSessionIdInvalid`, `BadSecureChannelIdInvalid`, subscriptions not transferable), dispose and recreate the session and re-create the subscription and monitored items from config. Backoff: initial 1 s, factor 2, max 15 s, ±20 % jitter (Microsoft.Extensions.Resilience). A 15 s cap keeps worst-case recovery within NFR-04's 30 s.

### Plant events and gaps

`Contracts.PlantEvent` (`schemaVersion`, `eventId`, `type` = `ConnectionLost | ConnectionRestored | Gap`, `plantId`, `lineId`, `assetId`, `endpoint`, `occurredAt`, `gapStart?`, `gapEnd?`, `reason?`). The adapter tracks the last source timestamp per asset; on `ConnectionRestored` it waits for the first sample per asset and emits `Gap(lastBefore, firstAfter)`. Events use the same store-and-forward queue with topic `plant.events`; the bridge creates that topic at startup (3 partitions, 30 days, ADR-0007).

## Risks / Trade-offs

- [Disk fills during a long outage] → limit and overflow policy are decision D3 (below).
- [Drain throughput lower than live rate after a long outage] → drain produces with in-flight pipelining and measures time-to-empty in the NFR-02 test; tune batch read size if needed.
- [Mode switch race between live publisher and drain loop] → a single publisher task owns the mode; producers enqueue to it via `Channel<T>`.

## Open Questions

- **D3 (blocker — resolve before apply):** maximum buffer size (hours of data or bytes) and the overflow policy: drop oldest batches, or stop subscribing (back-pressure to the source). The answer changes the `edge-buffering` spec (a new requirement for the limit) and tasks, so it must be decided and recorded as an ADR first.
