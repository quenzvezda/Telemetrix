# M2 group 2 review: OPC UA subscription

Change: `add-opcua-subscription`, tasks 2.1–2.4.
Branch: `m2-add-opcua-subscription`.
Tasks 2.1–2.4 are implemented and verified. The reviewer supplied successful static, bogus-NodeId and browse logs, confirmed all 33 tests and strict validation, and authorized group 3 on 2026-10-18.

## What changed and why

- `ISourceAdapter` exposes a channel writer for samples, shared with the future MQTT source and batcher (ADR-0006).
- `OpcUaSourceAdapter` uses one session, one subscription and one monitored item per configured or discovered tag. SDK notifications become immutable samples; synchronous callback writes honor channel back-pressure and cancellation. Sequential publishing preserves notification order.
- A failed monitored item is logged per tag with its raw server status code. Other tags continue.
- `OpcUaIngestionWorker` drains a bounded channel and logs every sample with structured fields. A source failure completes the channel and terminates the worker; shutdown cancels and awaits the source.
- Browse accepts an `Objects/...` path or NodeId, follows forward hierarchical references and SDK continuation points, and visits each local NodeId once. Tags use variable browse names; assets use their nearest parent object. A configured browse start node takes precedence over the static list. This naming and verification interpretation was confirmed by the reviewer on 2026-10-18 and recorded in the M2 spec/design.

Read the task commits in order with `git log --oneline main..HEAD`. Focus the diff
on `Sources/ISourceAdapter.cs`, `Sources/OpcUa/`, `OpcUaIngestionWorker.cs`,
the Bridge tests and the updated browse scenario under the OpenSpec change.
The first three commits on this branch belong to accepted group 1.

## Verification evidence

- Task 2.1: 24 Bridge tests passed, including Good, Uncertain, Bad, boolean true/false and missing-value notification mapping. Source/server timestamps and the raw status code are preserved.
- Task 2.2: on 2026-10-18, a temporary verification harness ran the adapter against the existing OPC PLC container at `opc.tcp://localhost:50000`. It appended `Pump01/Bogus` at `ns=3;s=DoesNotExist` to the seven development tags. The adapter logged `BadNodeIdUnknown` (`0x80340000`) for that tag while delivering 37 samples from all seven healthy tags; the harness then cancelled the adapter and exited successfully. The server accepted sampling 500 ms, publishing 1,000 ms and queue size 10. `Tank01/Temperature` retained Uncertain status `0x40900000`.

- Task 2.3: the actual Bridge executable ran against OPC PLC and logged samples for all seven configured tags, including boolean 1/0, UTC source/server timestamps, Good, Uncertain and Bad status codes. Its 1,000 ms publishing interval was confirmed in the startup log. The bounded verification run was stopped after observing samples; session recovery is task group 3. All 26 Bridge tests passed, including source-failure propagation and cancellation of the source when the worker stops.
- Task 2.4: on 2026-10-18, the adapter browsed `Objects/OpcPlc/Telemetry`, created 30 monitored items and delivered samples from all 30 discovered tags. A bounded harness observed 47 samples, checked that all seven static NodeIds were represented under their server browse names, then cancelled and awaited the adapter successfully. Examples were `Basic/AlternatingBoolean`, `Anomaly/DipData` and `Fast/BadFastUInt1`.

The generic OPC PLC folder also contains strings, ByteStrings and NodeId values.
These payload types are outside the numeric v1 contract: their sample values are
null, while timestamps and raw status codes remain unchanged. They are not
converted to a number or assigned a new quality by the Bridge. The seven static
mappings are all numeric/boolean; use them for the pumping-line path into M3.

## Repeat the static subscription checks

Run from the repository root with the existing OPC PLC container running:

```powershell
docker compose -f deploy/docker-compose.yml up -d opc-plc
dotnet run --project src/Telemetrix.Edge.Bridge
```

Expected: seven monitored items, sampling 500 ms, queue size 10, and one
subscription publishing every 1,000 ms. Each Sample log includes its identity,
value, UTC source/server timestamps and raw status code. OPC PLC reports changes
at different rates, so each publishing cycle may contain multiple samples for
some tags and none for unchanged tags. Stop with Ctrl+C.

To repeat the failure-isolation check, add an eighth tag through command-line
configuration without editing the development file:

In PowerShell, quote NodeId arguments because they can contain semicolons:

```powershell
dotnet run --project src/Telemetrix.Edge.Bridge -- --OpcUa:Tags:7:Asset=Pump01 --OpcUa:Tags:7:Tag=Bogus '--OpcUa:Tags:7:NodeId=ns=3;s=DoesNotExist'
```

Expected: `Pump01/Bogus` fails with `BadNodeIdUnknown` (`0x80340000`), while the
seven valid tags continue to produce samples. Stop with Ctrl+C.

The SDK's required certificate-store locations are under the current user's
local application data directory, `Telemetrix/pki`, outside the repository. M2
only selects security None and anonymous identity. Trust enforcement remains M9.

## Repeat browse discovery

```powershell
dotnet run --project src/Telemetrix.Edge.Bridge -- --OpcUa:BrowseStartNode=Objects/OpcPlc/Telemetry
```

Expected for the M1 OPC PLC image/configuration: one subscription with 30 requested
tags, using server names. Verify these seven NodeIds in the monitored-item logs:

| Static identity | Browse identity | NodeId |
| --- | --- | --- |
| Pump01/Running | Basic/AlternatingBoolean | `ns=3;s=AlternatingBoolean` |
| Pump01/FlowRate | Anomaly/DipData | `ns=3;s=DipData` |
| Pump01/DischargePressure | Basic/RandomSignedInt32 | `ns=3;s=RandomSignedInt32` |
| Pump01/MotorTemperature | Anomaly/PositiveTrendData | `ns=3;s=PositiveTrendData` |
| Pump01/Vibration | Anomaly/SpikeData | `ns=3;s=SpikeData` |
| Tank01/Level | Anomaly/NegativeTrendData | `ns=3;s=NegativeTrendData` |
| Tank01/Temperature | Fast/BadFastUInt1 | `ns=3;s=BadFastUInt1` |

The remaining discovered variables are also monitored. A different image or
simulator configuration may expose a different total. Stop with Ctrl+C.

## Automated checks and review boundary

```powershell
dotnet build -c Release
dotnet test --no-build -c Release
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
git diff --check main...HEAD
```

Verified on 2026-10-18: build has zero warnings/errors, all 33 Bridge tests pass, and all nine
OpenSpec items validate. The other test projects currently have no test cases;
this group does not exercise Kafka or TimescaleDB. No SQL, Kafka UI or Grafana
checks are needed for this group.

Approve this group before task 3.1 (session recovery) starts. The milestone's final
restart acceptance remains task 4.1. After group 3 implementation, the milestone
moves to In review for its final manual check; it is Done only after acceptance
and archival.
