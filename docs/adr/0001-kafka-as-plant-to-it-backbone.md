---
status: accepted
---

# Kafka sits between the plant and the database

The bridge never writes to the database; it produces batches to Kafka, and the transformer consumes them. This lets the edge keep collecting while the database (or transformer) is down, and lets the IT side be rebuilt or replayed from the topic without touching the plant. A single-node Kafka 4.x broker in KRaft mode is enough locally.

## Considered Options

- **Bridge writes straight to TimescaleDB** — simplest, but any database outage stops collection and pushes all buffering onto the edge.
- **MQTT broker as the backbone** — fine for fan-out, but no durable replayable log, consumer groups or per-key ordering.

## Consequences

Delivery is at-least-once, so the write path must be idempotent (ADR-0003).
