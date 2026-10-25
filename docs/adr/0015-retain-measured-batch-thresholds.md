---
status: accepted
---

# Retain the measured 500-sample or five-second batch thresholds

Keep 500 samples or five seconds per asset, whichever comes first, for D5.
The reviewer explicitly selected these final thresholds on 2026-10-25 after
reviewing the load-test comparison. Both candidates passed the full-duration latency and
throughput profiles on the recorded workstation, with zero dead-letter batches
and complete source-to-storage drain. See [the measurements and JSON evidence](../load-test.md).

At the default seven-tag OPC UA rate, 500/5 s had p95 7,774.686 ms and
1,000/2 s had p95 4,786.757 ms, both below 10 seconds. At the scaled MQTT rate,
500/5 s received 5,250.343 samples/s with p95 7,644.455 ms; 1,000/2 s received
5,242.643 samples/s with p95 21,240.268 ms. Both sustained 15 minutes without
growing consumer lag and stored all 4,884,600 publications including warm-up
and drain. The baseline meets the requirements while showing substantially
lower measured latency under load; the alternative's shorter age improves
low-rate latency but creates smaller, more frequent batches.

The throughput results require the documented bounded deployment profile:
1,024 Bridge batches and a 100,000-message outgoing broker queue, identical
for both candidates. Ordinary Bridge capacity remains 64 batches, and ordinary
simulator/broker defaults remain unchanged. Measurements include recorded host
contention and establish this workstation's result, not an unmeasured laptop's.

The selected thresholds match the existing defaults and the measured baseline;
no runtime or batching requirement change is needed. The passing baseline runs
provide the selected-default NFR-06 / NFR-07 evidence. Existing-M7-volume
migration verification and manual M8 acceptance remain separate reviewer steps.
