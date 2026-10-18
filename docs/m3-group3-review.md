# M3 group 3 review: batching and Kafka publishing

Change: `add-end-to-end-slice`, tasks 3.1–3.4.
Branch: `m3-add-end-to-end-slice`.
Status: implementation in progress.

## Task 3.1: per-asset batching

`AssetBatcher` preserves arrival order within each asset, flushes at 500 samples
or 5 seconds from the first sample, and flushes every open batch when its input
channel completes. A one-second `PeriodicTimer` uses the injected `TimeProvider`;
size flushes happen immediately. Each flush gets a new UUID v7 and UTC creation
time, while sample timestamps and raw status codes are preserved.

Cancellation aborts the pipeline; normal shutdown completes its input and drains
the output. A bounded output waits for the publisher, and bounded read turns let
the age timer run even when samples keep arriving.

`Microsoft.Extensions.TimeProvider.Testing` 10.11.0 is centrally versioned and used
only by Bridge unit tests. Source: [NuGet](https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing/10.11.0).

Verification on 2026-10-18: all 50 Bridge unit tests passed, including six batching
cases for mixed assets, size, age, quiet input, shutdown and output backpressure.
Group 2 reviewer acceptance is recorded with this task.

## Task 3.2: topic initialization

`TopicInitializer` creates `plant.telemetry.raw` with six partitions, replication
factor one for the local broker, and `retention.ms=604800000`. It treats only
`TopicAlreadyExists` as success and preserves existing topic settings; other
admin failures fail startup. Kafka bootstrap servers default to `localhost:29092`.

Verification on 2026-10-18: both `KafkaTopicTests` passed against Testcontainers
Apache Kafka 4.3.1 with broker auto-create disabled. They check first creation,
repeated startup and preservation of an existing two-partition topic with custom
retention. The targeted command is:

```powershell
dotnet test --project tests/Telemetrix.Integration.Tests/Telemetrix.Integration.Tests.csproj -c Release -- --filter-class Telemetrix.Integration.Tests.KafkaTopicTests
```
