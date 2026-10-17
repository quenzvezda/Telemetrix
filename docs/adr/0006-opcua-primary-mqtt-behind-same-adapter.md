---
status: accepted
---

# OPC UA is the primary source; MQTT shares the same pipeline

The bridge reads OPC UA first (subscriptions, status codes, source timestamps, browsable model — what PLCs speak) and MQTT only as a comparison source. Both sit behind one source-adapter interface that emits samples, so batching, buffering and producing are written once and the transformer cannot tell the sources apart beyond the batch's `source` field (FR-17).
