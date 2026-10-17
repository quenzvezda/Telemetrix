---
status: accepted
---

# SQLite is the edge store-and-forward buffer

When Kafka is unreachable the bridge spills batches to a local SQLite file and replays them in original order when Kafka returns (FR-15, NFR-02). SQLite gives a durable, ordered, transactional queue in one file with no extra service at the edge. Rejected: relying only on librdkafka's in-memory queue (lost on restart), and a file-per-batch spool (ordering and partial writes become our problem).

## Consequences

The buffer needs a size limit and an overflow policy — still an open decision (see ROADMAP.md).
