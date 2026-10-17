---
status: accepted
---

# Effectively-once = at-least-once delivery + idempotent insert

Kafka gives at-least-once delivery, so a batch may arrive twice after a crash or rebalance. We do not use Kafka transactions; instead `measurement` has `UNIQUE (tag_id, time)`, the transformer binary-`COPY`s a batch into a temporary staging table and runs `INSERT … SELECT … ON CONFLICT DO NOTHING`, and only then commits the Kafka offset (auto-commit disabled). A replay therefore inserts nothing twice, and a crash before commit only causes a harmless replay.

## Considered Options

- **Kafka exactly-once transactions** — do not cover the database write, which is the part that matters.
- **Row-by-row upsert** — idempotent but far too slow for NFR-07 (5,000 samples/s).

## Consequences

Two samples for the same tag with the same source timestamp are one measurement by definition; the second is silently dropped. Never commit an offset before the write succeeds (FR-24).
