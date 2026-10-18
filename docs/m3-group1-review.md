# M3 group 1 review: decisions and setup

Change: `add-end-to-end-slice`, tasks 1.1 and 1.2.
Branch: `m3-add-end-to-end-slice`.
Status: implementation and required verification complete; reviewer approval pending.

## What changed and why

- D1 is accepted in ADR-0011: the unknown-tag policy defaults to `dead-letter`, with `create` as an explicit opt-in. Only unknown-tag samples go to `plant.telemetry.dlq`, carrying the original batch id and reason `unknown-tag`; known-tag samples in the same batch are stored. This exposes configuration mistakes before they silently create metadata.
- D2 remains the accepted ADR-0010 decision: `double?` values throughout, booleans converted to 1/0 in source adapters, and strings out of scope for v1. M3's design now agrees with the shared `Sample` contract instead of promising a separate `unsupported-type` path.
- The roadmap marks M3 In progress and moves D1 to resolved decisions. The design source and M3 proposal, design, publishing spec, transform spec and task checklist reflect the accepted decisions.
- Central package management supplies the clients needed by M3. Both services reference Kafka; only the transformer references the database clients. Project references carry no versions.

| Package | Version | Service | Source |
| --- | --- | --- | --- |
| Confluent.Kafka | 2.16.0 | Bridge and Transformer | [NuGet](https://www.nuget.org/packages/Confluent.Kafka/2.16.0) |
| Npgsql | 10.0.3 | Transformer | [NuGet](https://www.nuget.org/packages/Npgsql/10.0.3) |
| Dapper | 2.1.89 | Transformer | [NuGet](https://www.nuget.org/packages/Dapper/2.1.89) |

## Diff review

Run from the repository root:

```powershell
git log --oneline --reverse main..HEAD
git diff main...HEAD -- ROADMAP.md docs/adr/0011-dead-letter-unknown-tags-by-default.md docs/plant-telemetry-pipeline.md openspec/changes/add-end-to-end-slice
git diff main...HEAD -- Directory.Packages.props src/Telemetrix.Edge.Bridge/Telemetrix.Edge.Bridge.csproj src/Telemetrix.Transformer/Telemetrix.Transformer.csproj
```

Read the commits in task order:

1. `fcaa75d` — `docs(openspec): record m3 unknown-tag policy and setup plan`: accepted decisions, roadmap, coherent M3 artifacts and task 1.1.
2. `build(deps): add m3 kafka and storage clients`: central versions, service references, task 1.2 and this review package.

Check that `create` is opt-in, known-tag samples continue through mixed batches,
nullable numeric values agree with ADR-0010, and database packages belong only to
the transformer.

## Manual verification

1. Build with the .NET 10 SDK:

   ```powershell
   dotnet build -c Release
   ```

   Expected: restore succeeds and build ends with zero warnings and errors.

2. Inspect resolved direct dependencies:

   ```powershell
   dotnet list src/Telemetrix.Edge.Bridge package
   dotnet list src/Telemetrix.Transformer package
   ```

   Expected: Bridge resolves Confluent.Kafka 2.16.0; Transformer resolves
   Confluent.Kafka 2.16.0, Npgsql 10.0.3 and Dapper 2.1.89. The existing hosting
   and OPC UA dependencies remain listed.

3. Validate OpenSpec and inspect progress:

   ```powershell
   npx -y @fission-ai/openspec@1.14.3 validate --all --strict
   npx -y @fission-ai/openspec@1.14.3 list
   ```

   Expected: all nine items validate; `add-end-to-end-slice` shows 2/14 tasks.

4. With Docker running, run the existing regression suite:

   ```powershell
   dotnet test --no-build -c Release
   ```

   Expected: the command exits 0 with 35 tests passed. Existing M2 integration checks start an OPC PLC
   container and exercise repeated server restarts, so allow several minutes.
   Transformer test cases will be added with its implementation; the existing
   empty-project exit-code allowance remains until task 6.0.

Kafka UI, Grafana and SQL acceptance checks become relevant when the publishing,
storage and observability stages are implemented. This group's review covers
decisions and build dependencies.

## Agent verification

On 2026-10-18: Release restore/build passed with zero warnings and errors;
the resolved direct dependencies matched the versions above; strict validation
passed all nine OpenSpec items; the full regression suite passed all 35 tests
with none failed or skipped, including both M2 OPC UA recovery integration cases.
The first suite invocation was interrupted by the shell's 120-second timeout;
the completed rerun used a six-minute command limit. The staged diff passed
`git diff --cached --check`. OpenSpec confirms tasks 1.1 and 1.2 complete,
with 12 tasks remaining. Group 2 (batch contract and serialization) awaits reviewer
approval under AGENTS.md's task-group checkpoint.
