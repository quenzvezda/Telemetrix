# M8 latency and throughput measurements

Status: both candidates passed the full 30-minute NFR-06 latency and 15-minute
NFR-07 throughput profiles on the recorded workstation. The reviewer accepted
500 samples / 5 seconds for D5 on 2026-10-25. Short smoke runs below remain diagnostic evidence.

## Machine and software

- Available developer workstation: Intel Core i5-12400F, 6 cores / 12 logical
  processors, 34,182,291,456 bytes of physical memory (about 31.84 GiB).
- Windows build 26200; .NET SDK 10.0.402 and runtime 10.0.13.
- Docker Desktop 4.51.0 / Engine 28.5.2, with 15.53 GiB available to its Linux VM.
- Kafka 4.3.1, TimescaleDB 2.30.2 / PostgreSQL 17, Mosquitto 2.1.2.
- OpenTelemetry 1.18.0, Prometheus ASP.NET Core exporter 1.18.0-beta.1.

These measurements describe this workstation. They do not establish a result
on a separate laptop; the reviewer should consider the recorded hardware when
accepting the roadmap's developer-laptop performance target.

## Repeatable profiles

Run from the repository root with Docker available:

```powershell
./deploy/Test-M8Load.ps1 -Kind latency -MaxSamples 500 -BatchSeconds 5
./deploy/Test-M8Load.ps1 -Kind throughput -MaxSamples 500 -BatchSeconds 5
./deploy/Test-M8Load.ps1 -Kind latency -MaxSamples 1000 -BatchSeconds 2
./deploy/Test-M8Load.ps1 -Kind throughput -MaxSamples 1000 -BatchSeconds 2
```

The script builds Release code and runs an explicit integration profile.
Normal CI does not execute these long performance runs. Each run uses new,
disposable Kafka and TimescaleDB containers; the throughput run also creates
its own Mosquitto broker. No existing Compose database or credentials are used.
The rollup/retention migration is installed, including its scheduled jobs.
Bridge and Transformer run through their actual application factories, with
JSON logging at Warning and Prometheus export enabled.

Latency uses the ordinary seven-tag PlantSim OPC UA source at 1 Hz for 1,800
measured seconds. Throughput uses 150 MqttSim pumping-line clones at 5 Hz:
150 × 7 × 5 = 5,250 offered samples/s, for 900 measured seconds. This gives
the generator 5% headroom for timer jitter; acceptance still requires at least
5,000 actually received samples/s. The first
line keeps its original identities; further line/asset identifiers get unique
suffixes. The disposable catalog is seeded before Transformer starts, keeping
the default unknown-tag dead-letter policy. Publications use QoS 1, retain,
and at most 64 in-flight operations. Every source tick completes before the
next begins, preserving increasing source timestamps per tag.

Both profiles warm up for 30 seconds before recording their UTC start/end
and monotonic elapsed time. The latency profile uses the ordinary 64-batch
queue; throughput explicitly uses `Ingestion:BatchQueueCapacity=1024` to absorb
many assets flushing together, with the same capacity in both D5 comparisons.
The default remains 64; the supported configurable range is 1-4096 batches.
All queues remain bounded and the edge disk quota/overflow policy is unchanged.

The report records received and published rates separately, write count,
source-window p95, dead-letter count, and five-second lag observations from
the actual metrics. The lag sampler itself runs every ten seconds. Rate
acceptance uses the lower of acknowledged source publications and received
samples; broker-side loss cannot be hidden behind a small Kafka lag.
The publisher completes its final tick, Bridge drains, and the database count
is checked against the acknowledged publication count (or accepted OPC UA
samples). Drain waits are bounded and incomplete drain fails the profile.

For the full throughput run, lag must have no sustained upward trend:
regression slope ≤ 0.01 batches/s and the final-minute mean no more than six
batches above the first-minute mean, allowing sampling across six partitions.
The report includes every observation, both means, the slope and maximum lag.
Latency requires p95 below 10,000 ms over measurements whose source timestamps
fall in the exact recorded run window. Short profiles still check actual rate,
latency and drain, but cannot establish the full-duration lag criterion.

Use `-Seconds 60` for a smoke run. It is always labelled `acceptanceDuration=false`
in its JSON report. Reports default to ignored `artifacts/m8-*.json`; pass
`-Report` to select a different output file. No report contains connection strings.

To run multiple isolated profiles from one prebuilt Release output without
Windows DLL-copy locks, build once with
`dotnet build tests/Telemetrix.Integration.Tests -c Release --artifacts-path artifacts/m8-runner`,
then pass `-Runner artifacts/m8-runner/bin/Telemetrix.Integration.Tests/release/Telemetrix.Integration.Tests.dll`.
The acceptance session runs both low-rate latency profiles side by side on
separate disposable stacks while throughput profiles run sequentially. This
records latency under additional host load, rather than claiming an idle-machine
benchmark. Each latency profile keeps the same default seven-tag input rate.

## Diagnostic findings

The initial 60-second throughput smoke received 3,187.976 samples/s with nearly
zero Kafka lag. A smaller 20-second reproduction confirmed that MqttSim
acknowledged 100,146 publications while Bridge received only 69,190 samples.
Mosquitto logged `Outgoing messages are being dropped` for the load client.
This happened before Kafka, explaining why Kafka lag alone looked healthy.

The original batch queue held 64 batches. A unit regression reproduces the
admission stall when many assets flush together and publication waits: a
1,700-sample source burst cannot complete with that fixed queue, despite a
larger configured capacity. The test failed in 2.180 seconds before the worker
honored the configuration. The selected load profile keeps 500/5 s unchanged
and tests a larger bounded batch queue as the single tuning variable.
Mosquitto's finite outgoing queue remains part of the source boundary described
in ADR-0014; its limits are documented in the
[official configuration reference](https://mosquitto.org/man/mosquitto-conf-5.html).

The first full baseline run received 5,003.485 samples/s for 900.016 seconds,
with stable Kafka lag and no dead letters, but **failed** the end-to-end drain
check: 4,656,652 acknowledged publications versus 4,655,274 stored measurements.
Mosquitto logged outgoing queue drops. Its default 1,000-message queue only
absorbs about 0.2 seconds at the offered rate. A successful short smoke therefore
did not establish loss-free sustained delivery.

The repeated throughput comparison explicitly sets the disposable broker's
`max_queued_messages` to 100,000 (about 20 seconds of offered traffic), keeping
the same 1,024-batch Bridge queue and broker settings for both candidates.
This is a bounded benchmark deployment setting, not a change to the default
development broker or the batching decision. The report records the broker
capacity; final source acknowledgement/storage equality remains mandatory.
The low-rate latency runs use the ordinary broker-independent OPC UA source.

An intermediate repeated baseline was stopped after the source rate fell below
5,000/s following host scheduling pauses. At 5,005 nominal samples/s the original
generator had only 0.1% headroom; its periodic timer coalesces missed ticks rather
than inventing historical samples. The final comparison uses 150 clones for
both candidates, records its actual offered/received rate, and keeps the same
900-second measurement duration. The interrupted run is not acceptance evidence.

## Acceptance results

The configured burst regression passed in 0.202 seconds after the worker used
the requested bounded capacity. With only throughput queue capacity changed to
1,024, the 20-second diagnostic run received 5,014.484 samples/s and drained all
152,152 source publications to the same database measurement count, with zero
dead-letter batches. This is a successful smoke result, not full acceptance.

The full-duration latency results are:

| Batching | UTC measurement window, 2026-10-25 | Seconds | p95 | Final accepted / stored | Dead letters | Result |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| 500 / 5 s | 05:52:03.568–06:22:03.573 | 1,800.005 | 7,774.686 ms | 12,824 / 12,824 | 0 | Pass |
| 1,000 / 2 s | 05:52:06.332–06:22:06.344 | 1,800.012 | 4,786.757 ms | 12,824 / 12,824 | 0 | Pass |

Both used the default seven-tag OPC UA source, 64-batch queue and six raw
partitions. Kafka lag remained zero in every recorded observation. Latency
includes the concurrent host load described above. Full JSON evidence,
including every lag observation, is in
[baseline latency](evidence/m8/latency-baseline.json) and
[alternative latency](evidence/m8/latency-alternative.json).

The final baseline throughput run passed:

| Batching | UTC measurement window, 2026-10-25 | Seconds | Actual received/s | Lag slope (batches/s) | First / last minute mean lag | Final published / stored | Dead letters |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 500 / 5 s | 06:19:46.586–06:34:46.593 | 900.007 | 5,250.343 | -0.018977 | 44.000 / 0.615 | 4,884,600 / 4,884,600 | 0 |
| 1,000 / 2 s | 06:35:35.945–06:50:35.954 | 900.008 | 5,242.643 | -0.000156 | 1.500 / 1.308 | 4,884,600 / 4,884,600 | 0 |

Maximum observed lag was 133 batches; the backlog drained rather than growing.
The source-time measurement window contains 4,725,000 measurements. Final counts
also include warm-up and the completed last source tick. Both source and storage
drain checks passed. The actual reception rate exceeds 5,000/s, independently
of the configured nominal rate. Full evidence is in
[baseline throughput](evidence/m8/throughput-baseline.json).

The baseline's early window overlapped low-rate latency profiles and briefly
the interrupted runners before their child processes were explicitly stopped.
This is recorded host contention; the result is not an idle-machine benchmark.
The alternative throughput run follows sequentially on the same machine and
uses the same final source, broker and application queue settings.

The alternative also passed full-duration throughput acceptance, with maximum
lag three batches and complete drain. Its source-time window also contains
4,725,000 measurements. Full evidence is in
[alternative throughput](evidence/m8/throughput-alternative.json).

## D5 comparison and accepted decision

| Batching | Default-rate p95 | High-rate p95 (diagnostic) | Actual throughput | Full source/storage equality | NFR-06 / NFR-07 |
| --- | ---: | ---: | ---: | --- | --- |
| 500 / 5 s | 7,774.686 ms | 7,644.455 ms | 5,250.343/s | Yes | Pass / Pass |
| 1,000 / 2 s | 4,786.757 ms | 21,240.268 ms | 5,242.643/s | Yes | Pass / Pass |

The reviewer accepted **500 samples / 5 seconds per asset, whichever comes first**, on 2026-10-25.
It satisfies the default-rate latency bound and the sustained throughput target,
with substantially lower measured latency under load. The shorter alternative
improves low-rate latency but flushes smaller batches more often; low Kafka lag
does not reveal waiting earlier in the pipeline. The measurements establish the
latency trade-off, not a profiler-based attribution of every queueing delay.

NFR-06 is evaluated at the default simulation rate, so the alternative's
21.240-second high-rate p95 is a diagnostic drawback rather than a failure of
that specified latency scenario. Both candidates pass the actual NFR-07 lag,
throughput, dead-letter and complete-drain checks. The host-contention caveat
above applies when interpreting the numerical comparison.

The decision is accepted in [ADR-0015](adr/0015-retain-measured-batch-thresholds.md).
The selected thresholds retain the existing defaults and measured baseline, so
no configuration change or repeated acceptance run is required. The reviewer
approved M8 archival on 2026-10-25 and deferred existing-volume migration and
manual dashboard testing until M9 is complete; those checks remain unverified
and are tracked as M9 tasks 5.1 and 5.2.
