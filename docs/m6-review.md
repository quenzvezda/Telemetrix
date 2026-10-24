# M6 PlantSim review

Change: `add-plant-simulator`.
Branch: `m6-add-plant-simulator`.
The reviewer authorized autonomous implementation of all groups on 2026-10-24.

## Groups 1–2: value model and OPC UA server

Seven unit cases passed: seeded 600-step normal ranges, seed repeatability,
correlated pump stop/start, and timed Bad quality/single-update spikes.
Three integration cases passed: anonymous SecurityMode None connection,
namespace 2 with seven typed tags and engineering units, and increasing UTC
source timestamps for all tags over four observations.

The reviewer connected UaExpert to `opc.tcp://localhost:4840` and supplied a
screenshot showing five Pump01 tags and two Tank01 tags, all Good, with Boolean
Running and Double numeric values. Numeric values changed as expected.
UaExpert's default StatusValue data-change filter does not notify a timestamp-only
change while Running stays true. Read the attribute directly or use a
StatusValueTimestamp filter to inspect each source timestamp.

## Group 3: fault injection

TripPump, StartPump, SetBadQuality, Spike and RestartServer are exposed under
`Objects/Plant01/Line01/Simulation`. Integration tests invoke them over OPC UA,
including invalid tag/Boolean-spike requests, invalid restart delay, quality
recovery and one-update spike replacement. Restart boundary tests use 0 and 5
seconds and require the old session to fail and a new connection within 10 seconds.
The SDK's default five-second shutdown countdown is disabled so it does not add
an undocumented delay to the requested outage.

The scheduler tests advance an injected clock through six simulated hours.
The default seed triggers every random fault and automatic pump restarts; the
same seed repeats the schedule. Disabled mode and zero rates emit no faults.
The PlantSim unit suite has ten passing cases.

## Groups 4–5: deployment and pipeline

The Dockerfile publishes a Release application and runs as the non-root app
user with a writable persistent PKI directory. Compose exposes port 4840 and
disables random faults by default for repeatable manual checks.
`appsettings.PlantSim.json` provides the host-side endpoint and all seven NodeIds.
The Bridge profile unit test loads and validates that configuration.

`PlantSimPipelineTests` builds the production Dockerfile and creates disposable
PlantSim, Kafka and TimescaleDB containers on dynamically assigned host ports.
It runs the production Bridge and Transformer registrations, waits for all
seven stored tags and asserts that Boolean Running is stored as 1.
It calls RestartServer through OPC UA, measures server readiness from timestamped
container logs within 10 seconds, then requires two ordered lost/restored/gap
sequences and all seven fresh stored tags within 30 seconds of readiness.
The test cleans up its own containers and buffer; it does not use the local
Compose database or its credentials.

No C# production file under Edge.Bridge or Transformer changes in M6.

The reviewer also reconnected UaExpert to the Compose container and confirmed
the Simulation object with all five methods on 2026-10-24, supplying a screenshot.

## Verification refinements

The complete suite exposed a Testcontainers startup race: its generated Kafka
script was executed while the Docker copy still held a write handle, producing
exit 126 / Text file busy. The shared delivery-test environment now interprets
that script through /bin/sh. The new production image build/acceptance case runs
without concurrent integration collections so it does not compete with existing
broker/source restart deadlines. Permanent metadata errors still fail and now
report their actual code.

The existing capacity-pause recovery test incorrectly required new source
timestamps even though its OPC PLC server stays alive. Unchanged values can
retain an older source timestamp. That case now proves the channel is drained
and empty while collection is paused, then requires every configured tag after
resume within the same 30-second deadline. Server-restart cases still reject
stale source timestamps. No production source-adapter behavior was changed.

## Acceptance evidence, 2026-10-24

| Check | Recorded result |
| --- | --- |
| Local Release build | Zero warnings and errors |
| Complete local suite | 182 passed, zero failed/skipped |
| Strict OpenSpec validation | All 12 active change/spec items passed |
| Container pipeline | Seven stored tags, then seven fresh tags and six ordered plant events after RestartServer; 7.333 seconds from server readiness in the focused run |
| Restart boundary cases | Delays 0 and 5 seconds both restored new connections within 10 seconds |
| UaExpert | Reviewer screenshots confirmed seven typed Good tags and all five Simulation methods |
| Release CI | All four jobs passed in [run 40097367673](https://github.com/quenzvezda/Telemetrix/actions/runs/40097367673), including build/test, strict OpenSpec, M4 SIGKILL and M5 outage regression |

The reviewer accepted M6 on 2026-10-24 and authorized merging
[PR #3](https://github.com/quenzvezda/Telemetrix/pull/3).
All 13 tracked tasks are complete. The change is archived at
[`2026-10-24-add-plant-simulator`](../openspec/changes/archive/2026-10-24-add-plant-simulator/),
its seven requirements are synced into main specs, and M6 is **Done**.
This closeout is included in PR #3.

Review the model and tag definitions first, then PlantNodeManager and
SimulationMethods, the worker restart lifecycle, and finally the Bridge profile
and PlantSimPipelineTests. Main task commits are:

- Model/test project: `bedb0d0`, `5860b1b`.
- Server/address space/timestamps: `8cc190d`, `4a0348a`, `79b3605`.
- Fault methods, scheduler and guide: `7f48c34`, `6c3ef15`, `cd38a66`.
- Deployment/profile and pipeline verification: `05ba757`, `0259bda`, `9d18abb`, `149af77`.
- Deadline and test-harness refinements: `fbefbab`, `36d320e`, `d7cdfea`.

## Repeatable automated checks

Run from the repository root with Docker running:

```powershell
dotnet build -c Release
dotnet test --no-build -c Release
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
```

For a focused check:

```powershell
dotnet test tests/Telemetrix.PlantSim.Tests -c Release
dotnet test tests/Telemetrix.Integration.Tests -c Release --filter FullyQualifiedName~PlantSim
```

Expected: zero warnings/errors, all tests pass without skips, and all OpenSpec
items pass strict validation. The pipeline case builds the image automatically.

## Manual UaExpert checks

```powershell
docker compose -f deploy/docker-compose.yml up -d --build plantsim
```

1. Connect to `opc.tcp://localhost:4840`, None / None, Anonymous.
   If discovery offers the container hostname, use the discovery URL as the
   connection address.
2. Browse `Objects/Plant01/Line01`. Confirm five Pump01 tags and two Tank01 tags.
   Drag them to Data Access View; expect Good, correct types and changing numeric
   values. Inspect a numeric tag's EngineeringUnits property.
3. Browse Simulation. Call TripPump, then StartPump; expect Running false/true,
   flow zero when stopped, and temperature/level following the documented dynamics.
4. Call SetBadQuality with `Tank01.Temperature` and UInt32 `30`; expect
   BadSensorFailure followed by Good after the interval.
5. Call Spike with `Pump01.Vibration`; expect one update above 4.5 mm/s,
   followed by normal values.
6. Call RestartServer with UInt32 `5`; expect the session to drop, the server to
   return within 10 seconds and UaExpert to reconnect.

## Manual Bridge and storage check

With the normal Kafka/TimescaleDB stack and Transformer already configured as in
the root README, switch only the Bridge environment:

```powershell
dotnet run --no-launch-profile --project src/Telemetrix.Edge.Bridge -- --environment PlantSim
```

All seven PlantSim tags should reach storage. Run this read-only query against
the local telemetry database:

```sql
SELECT t.asset_id, t.name, count(*) AS measurements, max(m.time) AS latest
FROM measurement m JOIN tag t USING (tag_id)
GROUP BY t.asset_id, t.name
ORDER BY t.asset_id, t.name;
```

Counts depend on retained history; verify new source timestamps after starting
this check. Running may produce only its initial sample until its value changes.
Trigger RestartServer in UaExpert and inspect
[plant.events in Kafka UI](http://localhost:8080/ui/clusters/local/all-topics/plant.events/messages):
each asset must receive ConnectionLost, ConnectionRestored, then Gap with
increasing UTC bounds. Fresh tags resume within 30 seconds of server readiness.
Grafana remains at http://localhost:3000; dashboards are part of M8.

For a Bridge on the Compose network, override only
`OpcUa__Endpoint=opc.tcp://plantsim:4840`. No reset of database or Kafka volumes
is needed. The automated pipeline check is the isolated alternative when local
ports are occupied.
