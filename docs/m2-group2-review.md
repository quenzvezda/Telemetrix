# M2 group 2 review: OPC UA subscription

Change: `add-opcua-subscription`, tasks 2.1–2.4.
Branch: `m2-add-opcua-subscription`.
Implementation is in progress; reviewer acceptance is pending.

## Verification evidence

- Task 2.1: 24 Bridge tests passed, including Good, Uncertain, Bad, boolean true/false and missing-value notification mapping. Source/server timestamps and the raw status code are preserved.
- Task 2.2: on 2026-10-18, a temporary verification harness ran the adapter against the existing OPC PLC container at `opc.tcp://localhost:50000`. It appended `Pump01/Bogus` at `ns=3;s=DoesNotExist` to the seven development tags. The adapter logged `BadNodeIdUnknown` (`0x80340000`) for that tag while delivering 37 samples from all seven healthy tags; the harness then cancelled the adapter and exited successfully. The server accepted sampling 500 ms, publishing 1,000 ms and queue size 10. `Tank01/Temperature` retained Uncertain status `0x40900000`.

- Task 2.3: the actual Bridge executable ran against OPC PLC and logged samples for all seven configured tags, including boolean 1/0, UTC source/server timestamps, Good, Uncertain and Bad status codes. Its 1,000 ms publishing interval was confirmed in the startup log. The bounded verification run was stopped after observing samples; session recovery is task group 3. All 26 Bridge tests passed, including source-failure propagation and cancellation of the source when the worker stops.

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
