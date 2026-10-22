---
status: accepted
---

# Bound the edge buffer and pause collection on overflow

The reviewer accepted D3 on 2026-10-22: retain at least one hour of the default
sample load, make capacity configurable, and stop subscribing temporarily when
the buffer fills rather than discarding accepted batches (FR-15, NFR-02).

Use a 64 MiB default quota for serialized payload bytes. Capacity is not an age
limit: old batches never expire. A sizing test serializes the default seven-tag
load with five-second per-asset batches and proves an hour fits. SQLite pages,
indexes and WAL require additional disk space; the quota is not a filesystem limit.

Pause collection at 80% utilization and resume at 50%, leaving headroom for
already accepted samples and open batches. At the hard quota a pending append
waits for replay to free space; it never deletes a batch. Bounded channels apply
back-pressure. A batch larger than the entire quota is a configuration error.
The replay loop remains independent of admission, so a full buffer can drain.

Pausing collection deliberately creates a gap at the source: changes while
subscription is stopped cannot be recovered without source history. Publish
connection-lost, connection-restored and gap events with reason `buffer-capacity`
through the same durable path. These events retain FIFO order with batches.

The zero-loss guarantee applies to accepted samples within the configured
capacity and the 30-minute default-load Kafka outage acceptance test. It does
not promise infinite outage retention or recover changes never collected.
Keep the existing spill-on-failure architecture (ADR-0005): a process killed
before an in-memory sample or pending delivery reaches SQLite can still lose it;
M5 restart verification specifically covers persisted batches.

Rejected: dropping the oldest batch, which erases already accepted samples;
unbounded disk growth, which can exhaust the edge host; and expiry by age,
which discards data even when storage remains available.
