# M2 group 3 review: OPC UA session recovery

Change: `add-opcua-subscription`, task 3.1.
Branch: `m2-add-opcua-subscription`.
The reviewer accepted the code and final milestone check (task 4.1) on 2026-10-18.

## What changed and why

- `OpcUaSessionRecovery` owns the active session. A bad keep-alive triggers the SDK's `SessionReconnectHandler`; the SDK retries, reactivates the session or recreates it and its subscription. Keep-alive runs every 5 seconds, and the reconnect base period is 1 second using the injected `TimeProvider`.
- When the SDK supplies a replacement session, recovery moves ownership to it, registers one keep-alive handler and disposes the previous session. The same channel and ingestion worker keep running.
- Shutdown detaches keep-alive, disposes the reconnect timer and closes/disposes the active session. A late replacement is disposed instead of restarting collection. Session-close errors caused by an unavailable server are debug diagnostics during shutdown.
- Callback failures propagate to the adapter/worker instead of leaving collection silently stuck.
- Two integration test cases use an isolated OPC PLC container: static mappings and browse discovery. Each verifies two server restarts, fresh samples for all seven development tags, and cancellation while the server is offline and recovery is active. The host port is selected once and pinned so Docker stop/start cannot change the configured endpoint. Startup waits for the OPC UA server's ready log as well as its TCP port.

Reconnect tuning, gap/connection plant events and the M5 latency guarantee remain
in M5. M2 logs basic interruption/restoration diagnostics and does not synthesize
samples for the outage.

SDK reference: [OPC Foundation client example, version 2.0.1](https://github.com/OPCFoundation/UA-.NETStandard/blob/2.0.1/samples/Reference/ConsoleReferenceClient/UAClient.cs).

## Diff review

```powershell
git log --oneline -1
git show --stat HEAD
git show HEAD -- src/Telemetrix.Edge.Bridge/Sources/OpcUa tests/Telemetrix.Integration.Tests Directory.Packages.props
```

Focus on active-session ownership after recreation, keep-alive registration,
shutdown ordering, and the integration test's source-timestamp checks. Buffered
samples from before a restart cannot satisfy those checks.

## Agent verification evidence

On 2026-10-18, a bounded verification harness launched the actual Release Bridge
executable with development configuration and restarted the existing Compose
`opc-plc` service twice. The Bridge PID remained `38944` throughout. Both recoveries
reported one subscription, with new active session ids, and all seven tags
resumed with source timestamps newer than completion of the restart command.
They were observed 2.46 seconds and 2.53 seconds after the respective Compose
commands returned. These are local observations, not an M5 latency guarantee.
The harness terminated its child Bridge after the checks.

The isolated integration tests also passed for static and browse modes, including
shutdown while recovery was active. They create and remove their own containers
and do not restart the Compose stack.

Final checks passed: Release build with zero warnings/errors, 33 unit cases and
two OPC UA integration cases, strict validation of all nine OpenSpec items, and
the diff whitespace check. The final focused integration run also required
source timestamps newer than the completed container restart and passed both
cases.

## Reviewer acceptance

On 2026-10-18, the reviewer supplied a continuous Bridge log showing seven
requested tags, a 1,000 ms publishing interval, an interruption with
`BadSecureChannelClosed`, and restoration with active session
`ns=7;i=896216852` and one subscription. Sample logs after restoration cover all
seven configured tags with advancing source timestamps. The application was
then shut down normally. This log proves one manual restart recovery; the
repeated-restart cases are also covered by the automated and agent checks above.
The reviewer confirmed code review, a successful Release build, all 35 tests,
strict validation of nine OpenSpec items and an empty diff whitespace check.

## Automated checks

Docker must be running for the two integration cases.

```powershell
dotnet build -c Release
dotnet test --no-build -c Release
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
git diff --check main...HEAD
```

Expected: zero build warnings/errors, 35 tests pass (33 unit cases and two OPC UA
integration cases), all nine OpenSpec items validate, and the diff check is empty.
The integration cases exercise OPC UA; Kafka and TimescaleDB tests come in later
milestones. Each recovery test has a two-minute deadline.

## Manual checks

Run all commands from the repository root.

1. Ensure the local OPC PLC is available:

   ```powershell
   docker compose -f deploy/docker-compose.yml up -d opc-plc
   ```

2. In terminal A, start the Bridge and keep it running:

   ```powershell
   dotnet run --project src/Telemetrix.Edge.Bridge
   ```

   Expected: one subscription, seven requested tags, publishing 1,000 ms,
   sampling 500 ms, queue size 10. All seven tags produce Sample logs carrying
   UTC source/server timestamps and raw status codes. Unchanged tags can have
   no notification in a publishing cycle.

3. In terminal B, record the Bridge PID and restart only OPC PLC:

   ```powershell
   Get-Process -Name Telemetrix.Edge.Bridge | Select-Object Id,StartTime
   docker compose -f deploy/docker-compose.yml restart opc-plc
   Get-Process -Name Telemetrix.Edge.Bridge | Select-Object Id,StartTime
   ```

   Expected in terminal A: an interruption diagnostic, then `OPC UA session
   restored` with one subscription. Fresh Sample logs resume for Pump01's
   Running, FlowRate, DischargePressure, MotorTemperature and Vibration, plus
   Tank01's Level and Temperature. The PID/start time remain unchanged, and the
   Bridge is never restarted. Source timestamps advance beyond the restart.

   SDK publish/channel errors may appear during the outage. Assess recovery by
   the restoration log and subsequent samples. Values from OPC PLC remain
   generic simulator values; numeric range validation belongs to M3.

4. Repeat the restart command while the same Bridge process runs. Expect the
   same recovery and all seven tags again. Stop terminal A with Ctrl+C.

5. Optional browse-mode check: start terminal A with the following command and
   repeat the restart from terminal B:

   ```powershell
   dotnet run --project src/Telemetrix.Edge.Bridge -- --OpcUa:BrowseStartNode=Objects/OpcPlc/Telemetry
   ```

   Expect the discovered tags to resume with server browse names, including
   `Basic/AlternatingBoolean`, `Anomaly/DipData` and `Fast/BadFastUInt1`.

6. Optional shutdown-during-outage check: stop OPC PLC, wait for the interruption
   diagnostic in terminal A, then press Ctrl+C in terminal A:

   ```powershell
   docker compose -f deploy/docker-compose.yml stop opc-plc
   ```

   Expected: the Bridge exits without waiting indefinitely for server recovery.
   Restore OPC PLC after the check:

   ```powershell
   docker compose -f deploy/docker-compose.yml up -d opc-plc
   ```

No SQL, Kafka UI or Grafana checks are needed for this group. Successful reviewer
confirmation of the default subscription and restart checks also supplies the
evidence for task 4.1. The reviewer confirmed it on 2026-10-18; archive/merge
follow the repository's milestone loop.
