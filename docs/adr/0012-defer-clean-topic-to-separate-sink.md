---
status: accepted
---

# Defer the clean topic to the separate sink

D4 is resolved on 2026-10-22: defer `plant.telemetry.clean` (FR-25) until the separate-sink stretch goal. The v1 transformer writes directly to TimescaleDB; M4 proves durable delivery and effectively-once storage without adding a second output whose success would also have to be coordinated with offset commits. When the separate sink is introduced, revisit the raw-to-clean publication boundary and its delivery guarantees together.
