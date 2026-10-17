---
status: accepted
---

# One batch per asset, keyed by asset id

The bridge groups samples into one batch per asset (flushed at 500 samples or 5 s, whichever first) and produces it with the asset id as the Kafka key. All of an asset's data lands in one partition, so it is processed in source-timestamp order (NFR-05), while different assets spread across partitions for throughput. Keying by tag would scatter an asset's tags across partitions; keying by line would cap parallelism at the number of lines.
