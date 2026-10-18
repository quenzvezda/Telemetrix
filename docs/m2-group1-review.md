# M2 group 1 review: contract and configuration

Change: `add-opcua-subscription`, tasks 1.1–1.3.
Branch: `m2-add-opcua-subscription`.
Reviewer acceptance is pending.

## What changed and why

- `Sample` is an immutable shared contract. Its constructor normalizes supplied source/server timestamps to UTC while preserving their instants; nullable numeric values retain the raw status code.
- D2 is accepted in ADR-0010: source adapters convert booleans to 1/0, and strings are out of scope for v1. The roadmap, design source and M2 artifacts agree with that decision.
- The bridge references `OPCFoundation.NetStandard.Opc.Ua.Client` 2.0.1 through central package management. Package source: [OPC Foundation on NuGet](https://www.nuget.org/packages/OPCFoundation.NetStandard.Opc.Ua.Client/2.0.1).
- `OpcUaOptions` binds the `OpcUa` configuration section and validates it at host startup. Errors name the setting. M2 accepts security mode None, positive intervals and queue size, and requires tag mappings or a browse start node.
- Plant and line identify every sample. Endpoint, identity, security mode, intervals, queue size, tag properties and browse start node can use the standard double-underscore environment variable convention, such as `OpcUa__Endpoint` or `OpcUa__Tags__0__NodeId`.
- Defaults are sampling 500 ms, publishing 1,000 ms and queue size 10. Base configuration has no tag mappings; the development file supplies all seven. Other environments must supply their own mappings or browse start node.

## Development mappings

NodeIds come from the reviewer's [M1 inventory](m1-opcua-verification.md).
These are generic simulator values mapped to the domain's tag names, not physical
simulations with the pumping line's ranges or correlations. Recheck namespace
index 3 against a different server/version. Live monitored-item verification
belongs to group 2.

| Asset | Tag | OPC PLC NodeId |
| --- | --- | --- |
| Pump01 | Running | `ns=3;s=AlternatingBoolean` |
| Pump01 | FlowRate | `ns=3;s=DipData` |
| Pump01 | DischargePressure | `ns=3;s=RandomSignedInt32` |
| Pump01 | MotorTemperature | `ns=3;s=PositiveTrendData` |
| Pump01 | Vibration | `ns=3;s=SpikeData` |
| Tank01 | Level | `ns=3;s=NegativeTrendData` |
| Tank01 | Temperature | `ns=3;s=BadFastUInt1` |

## Diff review

Run from the repository root:

```powershell
git log --oneline main..HEAD
git diff main...HEAD -- src/Telemetrix.Contracts/Sample.cs docs/adr/0010-numeric-sample-values-in-v1.md ROADMAP.md docs/plant-telemetry-pipeline.md openspec/changes/add-opcua-subscription
git diff main...HEAD -- Directory.Packages.props src/Telemetrix.Edge.Bridge tests/Telemetrix.Edge.Bridge.Tests
```

Read the commits in task order: contract and D2, dependency, then options and tests.
Check that timestamps come from callers, package versions remain central, the
seven mappings match the inventory, and startup validation is wired in `Program.cs`.

## Automated checks

```powershell
dotnet restore
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
```

Expected: restore succeeds; build has zero warnings/errors; 18 tests pass; all
nine OpenSpec items pass. The Bridge tests cover UTC preservation, null values
with raw status codes, loading the seven development tags, startup failures,
browse-only configuration and environment override of the JSON endpoint.
The other test projects currently have no test cases, so this run does not
exercise Kafka or TimescaleDB.

## Manual checks

These checks need only the .NET SDK. Docker, Kafka UI, Grafana and SQL checks
start with later groups/milestones that use those services.

1. Start with development configuration:

   ```powershell
   dotnet run --project src/Telemetrix.Edge.Bridge --no-build -c Release
   ```

   Expected: the launch profile selects Development, the host starts, and the
   existing template Worker logs periodically. Stop with Ctrl+C. Subscription
   and sample logging are task group 2.

2. Prove the endpoint error reaches the executable:

   ```powershell
   dotnet run --project src/Telemetrix.Edge.Bridge --no-build -c Release -- --OpcUa:Endpoint=https://localhost:50000
   ```

   Expected: non-zero exit at startup with `OptionsValidationException` naming
   `OpcUa:Endpoint` and the required `opc.tcp://` scheme.

3. Prove a missing NodeId is rejected:

   ```powershell
   dotnet run --project src/Telemetrix.Edge.Bridge --no-build -c Release -- --OpcUa:Tags:0:NodeId=
   ```

   Expected: non-zero exit at startup naming `OpcUa:Tags:0:NodeId` as required.

4. Prove the standard environment variable overrides the valid JSON endpoint:

   ```powershell
   $previousEndpoint = $env:OpcUa__Endpoint
   try {
       $env:OpcUa__Endpoint = 'https://localhost:50000'
       dotnet run --project src/Telemetrix.Edge.Bridge --no-build -c Release
   }
   finally {
       $env:OpcUa__Endpoint = $previousEndpoint
   }
   ```

   Expected: the same endpoint validation error, proving the environment value
   takes precedence. The automated test also proves a valid `opc.tcp` override
   resolves to `opc.tcp://opc-plc:50000`.

## Agent verification

On 2026-10-18: restore, Debug and Release builds, all 18 tests, strict validation
of all nine OpenSpec items, and executable checks 2 and 3 passed with the expected
outcomes. Checks 2 and 3 intentionally exit non-zero. Human review of this group
is pending; the milestone remains In progress.
