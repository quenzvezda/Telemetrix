# M3 group 2 review: batch contract and serialization

Change: `add-end-to-end-slice`, task 2.1.
Branch: `m3-add-end-to-end-slice`.
Status: implementation and required verification complete; reviewer approval pending.

## What changed and why

- `TelemetryBatch` carries the versioned envelope from the design example: schema version, caller-supplied batch id, source, endpoint, plant/line/asset ids, creation time and samples. `CurrentSchemaVersion` is 1. The constructor snapshots the sample collection so the batcher can later reuse its buffer without changing a published batch.
- `BatchSample` carries tag, nullable numeric value, source/server timestamps and the unsigned raw status code. Both records normalize supplied timestamps to UTC while preserving their instants.
- `TelemetryJson.Serialize` produces UTF-8 bytes and `Deserialize` reads them through a source-generated `TelemetryJsonContext`. The shared `Options` expose the same camelCase wire format for both services. A timestamp converter writes UTC with a `Z` suffix and preserves subsecond precision.
- Values remain numbers or null. Source adapters already convert booleans to 1/0 under ADR-0010. The numeric JSON contract rejects literal booleans and strings; schema validation and dead-letter routing are implemented by later tasks.
- Eleven new contract cases cover the design example, exact envelope/sample property names, timestamp offsets and precision, 1/0 values, null values, unsigned status codes, unsupported wire types, shared options and sample-buffer isolation.
- The round-trip test reads the JSON example directly from `docs/plant-telemetry-pipeline.md`, copied as a fixture at build time, so future edits to that example are checked against the implementation.
- Group 1 reviewer acceptance, including the supplied build, dependency and 35-test results, is recorded in its review package and the task checklist.

## Diff review

Run from the repository root:

```powershell
git log --oneline 06f467f..HEAD
git diff 06f467f..HEAD -- src/Telemetrix.Contracts
git diff 06f467f..HEAD -- tests/Telemetrix.Edge.Bridge.Tests/TelemetryJsonTests.cs tests/Telemetrix.Edge.Bridge.Tests/Telemetrix.Edge.Bridge.Tests.csproj
git diff 06f467f..HEAD -- docs/m3-group1-review.md docs/m3-group2-review.md openspec/changes/add-end-to-end-slice/tasks.md
```

The task commit is `feat(contracts): add versioned telemetry batch serialization`.
Read the two records, then the serializer/context/converter, then the tests.
Check these details:

1. The envelope matches the design example, and sample identity stays in the envelope rather than being repeated in each sample.
2. Source timestamps remain the measurement time. UTC conversion changes the offset, not the supplied instant; batch ids and creation times come from the caller.
3. Boolean values have already become numeric 1/0 before batching. Null values and raw status codes survive the round trip.
4. Both UTF-8 helpers and public shared options use the generated contract metadata and the same timestamp converter.

## Manual verification

1. Build with the .NET 10 SDK:

   ```powershell
   dotnet build -c Release
   ```

   Expected: successful restore/build with zero warnings and errors, including
   compilation of the generated JSON context.

2. Run the Bridge unit tests, which also host the shared contract tests:

   ```powershell
   dotnet test --project tests/Telemetrix.Edge.Bridge.Tests/Telemetrix.Edge.Bridge.Tests.csproj --no-build -c Release
   ```

   Expected: 44 tests pass. `TelemetryJsonTests` checks the design JSON example,
   `Z` timestamps, unchanged instants and precision, null/status preservation,
   numeric 1/0 values, unsupported types and collection isolation. Docker is not
   required for this command.

3. With Docker running, run the full regression suite:

   ```powershell
   dotnet test --no-build -c Release
   ```

   Expected: 46 tests pass, including both existing M2 OPC UA recovery cases.
   Allow several minutes for repeated container restarts.

4. Validate OpenSpec and inspect progress:

   ```powershell
   npx -y @fission-ai/openspec@1.14.3 validate --all --strict
   npx -y @fission-ai/openspec@1.14.3 list
   ```

   Expected: all nine items validate; `add-end-to-end-slice` shows 3/14 tasks.

Kafka publishing starts in group 3; Kafka UI and SQL acceptance checks will be
included in the review packages for the stages that use them.

## Agent verification

On 2026-10-18: Release build passed with zero warnings and errors; all 44 Bridge
unit tests passed, including the 11 new contract cases; strict OpenSpec validation
passed all nine items. The full regression suite passed all 46 tests with none
failed or skipped, including both OPC UA recovery integration cases. The staged
diff passed `git diff --cached --check`. OpenSpec confirms 3/14 tasks complete.
Group 3 (batching and Kafka publishing) awaits reviewer approval under AGENTS.md's
task-group checkpoint.
