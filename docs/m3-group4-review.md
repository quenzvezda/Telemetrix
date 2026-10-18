# M3 group 4 review: Transformer pipeline

Change: `add-end-to-end-slice`, tasks 4.1–4.3.
Branch: `m3-add-end-to-end-slice`.
Status: reviewer accepted verification and authorized group 5 on 2026-10-18.

## Task 4.1: quality and range checks

`QualityMapper` maps OPC UA severity bits 30–31 to Good (0), Uncertain (1) or
Bad (2); reserved severity values conservatively become Bad. `RangeCheck` makes
missing, non-finite or out-of-range values Bad, preserves an existing Uncertain
or Bad quality, and treats exact range boundaries as valid. Either range bound
can be absent. Values and timestamps are not changed by these functions.

Verification: all 20 Transformer unit cases passed, covering the three
severities, additional status bits, reserved severity, both bounds, inclusive
boundaries, absent bounds, missing values and non-finite input. Group 3 reviewer
acceptance and the leftover-process cleanup are recorded with this task.

## Task 4.2: tag resolution and dead-lettering

`TagResolver` loads its `(asset, tag)` cache once and refreshes on a miss. Under
the default `dead-letter` policy it separates unknown samples, preserving the
original envelope and batch id, while returning known samples with their tag
metadata in arrival order. The `create` opt-in inserts tags without unit/range;
Postgres uses parameterized SQL, `ON CONFLICT DO NOTHING RETURNING` and a re-select
when another creator wins. Catalog failures propagate rather than being
misclassified as unknown tags. Negative lookups are only cached within a batch.

Assets must already be provisioned in the schema's `asset` table: `create`
creates tags, not assets with invented types. An unprovisioned asset cannot be
resolved and its samples remain in the unknown-tag batch under either policy.

`KafkaDeadLetterPublisher` sends that subset as shared JSON with the original
batch id, asset key and `dlq.reason=unknown-tag`. The Transformer initializes
`plant.telemetry.dlq` at startup with one partition and 30-day retention, leaving
existing settings unchanged. Configuration validates `Kafka:BootstrapServers`
and `TagResolution:UnknownTagPolicy` before topic startup. The placeholder Worker
is removed. The executable initializes its owned topic; production raw consumption
and database startup loading are connected to the writer in task 5.2.

Verification: all 29 Transformer unit cases passed. Two targeted integration
cases passed: the actual TimescaleDB schema and seeded metadata, eight concurrent
tag inserts returning one identity, parameterized tag names, and Kafka DLQ
creation/repeated startup plus payload/key/header preservation. All database
mutations ran only inside disposable test containers, not the local compose DB.

## Task 4.3: grouped raw consumption

`RawBatchConsumer` is a `BackgroundService` whose blocking consume loop runs on
a dedicated long-running task. It joins group `transformer`, subscribes only to
`plant.telemetry.raw`, deserializes through `TelemetryJson`, and passes one batch
at a time to `IRawBatchHandler`, including its key, partition and offset. The
consumer closes on shutdown and propagates handler failures. It starts at the
earliest available offset when there is no committed offset. Client-default
auto-commit/store remain the explicit M3 limitation; commit-after-write and
schema dead-lettering are M4 work ([Confluent consumer documentation](https://docs.confluent.io/kafka-clients/dotnet/current/overview.html#auto-offset-commit)).

The handler boundary is where task 5.2 connects resolution, quality checks,
dead-letter publishing and the measurement writer. This group does not register
a raw consumer with a discard-only handler in the local executable. Running the
executable now initializes the DLQ and waits; it does not consume or commit any
local raw batches. The consumer is exercised with capturing/failing handlers
only in disposable integration environments until storage is ready.

Verification: both `RawBatchConsumerTests` passed. Kafka's admin metadata showed
two members in group `transformer`, each with three partitions, with every raw
partition assigned exactly once. Eighteen produced batches were delivered with
correct keys, payloads and per-partition order. Stopping one member transferred
all six partitions to the survivor, which handled a subsequent batch. A second
case proves a handler failure faults the consumer rather than skipping ahead.

## Commits and diff review

```powershell
git log --oneline --reverse 1b0c2f7..HEAD
git diff 1b0c2f7..HEAD -- src/Telemetrix.Transformer/Pipeline tests/Telemetrix.Transformer.Tests
git diff 1b0c2f7..HEAD -- src/Telemetrix.Transformer/Storage src/Telemetrix.Transformer/Consuming
git diff 1b0c2f7..HEAD -- src/Telemetrix.Transformer/Program.cs src/Telemetrix.Transformer/TransformerServiceCollectionExtensions.cs src/Telemetrix.Transformer/appsettings.json tests/Telemetrix.Integration.Tests docs/m3-group3-review.md openspec/changes/add-end-to-end-slice/tasks.md
```

Read the task commits in order:

- `d24f245` — quality mapping and valid ranges (4.1).
- `e752202` — tag resolution, catalog, DLQ creation and publishing (4.2).
- `feat(transformer): consume raw batches as one kafka group` — consumer and partition tests (4.3).

Check severity masking, inclusive bounds, preservation of Uncertain/Bad, keeping
only unknown samples in the DLQ batch, original batch ids, opt-in creation,
parameterized SQL and concurrent inserts. Consumer commits remain at M3 defaults;
do not read passing group-assignment tests as proof of M4 delivery guarantees.

## Manual verification

Run from the repository root. Stop Bridge/Transformer instances with Ctrl+C
before building, so Windows does not lock their output DLLs.

1. Build and run unit tests:

   ```powershell
   dotnet build -c Release
   dotnet test --project tests/Telemetrix.Transformer.Tests/Telemetrix.Transformer.Tests.csproj --no-build -c Release
   ```

   Expected: zero build warnings/errors and 29 Transformer unit cases passed.
   The tests need no Docker, Kafka connection or database credentials.

2. With Docker running, verify group partition ownership and takeover:

   ```powershell
   dotnet test --project tests/Telemetrix.Integration.Tests/Telemetrix.Integration.Tests.csproj --no-build -c Release -- --filter-class Telemetrix.Integration.Tests.RawBatchConsumerTests
   ```

   Expected: two cases pass. They check the broker's real group metadata and
   received batches, not a simulated partition-assignment algorithm.

3. Verify the catalog and DLQ against disposable infrastructure:

   ```powershell
   dotnet test --project tests/Telemetrix.Integration.Tests/Telemetrix.Integration.Tests.csproj --no-build -c Release -- --filter-class Telemetrix.Integration.Tests.PostgresTagCatalogTests --filter-class Telemetrix.Integration.Tests.DeadLetterTests
   ```

   Expected: two cases pass, with seeded tag ranges, concurrent creation,
   one-partition/30-day DLQ, original batch id/key and `dlq.reason=unknown-tag`.
   Test containers use generated connection settings and are disposed afterwards.

4. Run all checks and inspect OpenSpec progress:

   ```powershell
   dotnet test --no-build -c Release
   npx -y @fission-ai/openspec@1.14.3 validate --all --strict
   npx -y @fission-ai/openspec@1.14.3 list
   ```

   Expected: 93 tests pass, all nine OpenSpec items validate, and M3 shows
   10/14 tasks complete. Allow several minutes for the existing OPC UA restart
   tests and Kafka group rebalances.

5. With the local compose Kafka and Kafka UI running, start Transformer:

   ```powershell
   dotnet run --project src/Telemetrix.Transformer --no-build -c Release
   ```

   Expected: a created-DLQ or existing-DLQ log, followed by normal host startup.
   No database credentials are needed for this group's topic-only executable.
   Open [Kafka UI topics](http://localhost:8080/ui/clusters/local/all-topics),
   select `plant.telemetry.dlq`, and inspect its Overview and Settings tabs.
   A new topic has one partition, replication factor one and
   `retention.ms=2592000000` (30 days). An existing topic keeps its settings.
   Stop with Ctrl+C and confirm the terminal prompt returns.

The local executable does not yet produce DLQ batches or join the consumer group;
those paths are proven by container tests in this group and wired to the writer
in 5.2. No manual SQL mutation is needed. SQL measurement checks start in group 5,
and Grafana dashboard checks belong to M8.

## Agent verification and checkpoint

On 2026-10-18, Release build passed with zero warnings and errors. All 93 tests
passed with no failures or skips: 55 Bridge unit cases, 29 Transformer unit cases
and nine integration cases. Strict OpenSpec validation passed all nine items;
the staged diff passed `git diff --cached --check`. OpenSpec confirms tasks
4.1–4.3 complete and overall progress 10/14. No standalone Bridge/Transformer
process was launched by the agent during this group.

The reviewer confirmed the Release build, 29 Transformer unit cases, both raw
consumer cases, the catalog/DLQ cases, and local DLQ creation plus clean host
shutdown, then authorized group 5 on 2026-10-18. M3 remains In progress.
