---
status: accepted
---

# The OPC UA source timestamp is the measurement time; everything is UTC

`measurement.time` is the source timestamp reported by the plant, not the server or ingest time, so replays and late arrivals land at the right instant and `(tag_id, time)` stays a stable identity (ADR-0003). Server and ingest timestamps are kept only to diagnose clock drift and latency (NFR-06). All timestamps are stored and transported in UTC.
