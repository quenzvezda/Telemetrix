---
status: proposed
---

# Retain the measured 500-sample or five-second batch thresholds

Propose keeping 500 samples or five seconds per asset, whichever comes first,
for D5. The reviewer approved this M8 baseline on 2026-10-25; final selection
remains pending. Both candidates passed the same full-duration latency and
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

Do not mark this ADR accepted or close D5 until the reviewer selects the final
thresholds. Selecting different defaults requires aligning the batching
requirement and configuration and repeating the selected-default acceptance
runs before M8 acceptance.
