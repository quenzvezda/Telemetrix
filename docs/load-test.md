# M8 latency and throughput measurements

Status: profiles are being verified. Short smoke runs are diagnostic evidence,
not substitutes for the 30-minute NFR-06 or 15-minute NFR-07 acceptance runs.
D5 remains open for the reviewer after the comparison.

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
measured seconds. Throughput uses 143 MqttSim pumping-line clones at 5 Hz:
143 × 7 × 5 = 5,005 offered samples/s, for 900 measured seconds. The first
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

## Acceptance results

The configured burst regression passed in 0.202 seconds after the worker used
the requested bounded capacity. With only throughput queue capacity changed to
1,024, the 20-second diagnostic run received 5,014.484 samples/s and drained all
152,152 source publications to the same database measurement count, with zero
dead-letter batches. This is a successful smoke result, not full acceptance.

The full runs and D5 recommendation will be recorded here after measurement.
