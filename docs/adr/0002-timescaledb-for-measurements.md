---
status: accepted
---

# TimescaleDB stores measurements

Measurements live in a TimescaleDB hypertable partitioned by time, with continuous aggregates for 1-minute rollups and retention policies for raw data. Plain PostgreSQL would work but loses automatic partitioning, rollups and retention; a dedicated TSDB (InfluxDB) would lose the relational asset/tag metadata, foreign keys and `ON CONFLICT` that the idempotent write path relies on.
