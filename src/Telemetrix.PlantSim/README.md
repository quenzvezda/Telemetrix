# PlantSim

An OPC UA server for Plant01 / Line01, replacing the generic OPC PLC source.
It exposes seven read-only tags, updates their values and UTC source timestamps
once per second, and supports reproducible normal operation and fault injection.

## Run locally

From the repository root:

```powershell
dotnet run --project src/Telemetrix.PlantSim -- --Faults:Random:Enabled=false
```

Connect UaExpert to `opc.tcp://localhost:4840`, choose the None / None endpoint
and Anonymous identity. Browse `Objects/Plant01/Line01`; drag Pump01's five tags
and Tank01's two tags into Data Access View. Running is Boolean; the remaining
tags are Double. Numeric tags expose EngineeringUnits properties.

All string NodeIds use namespace index 2, for example
`ns=2;s=Line01.Pump01.FlowRate`. The namespace URI is `urn:telemetrix:plantsim`.
Normal ranges are flow 40–60 m³/h, pressure 3.5–5 bar, motor temperature 45–80 °C,
vibration 0.5–4.5 mm/s, tank level 20–90 %, and tank temperature 15–30 °C.
When stopped, flow is zero and pressure falls to 1 bar. Temperature and level
decrease while stopped and increase while running, within their normal bounds.

UaExpert's default StatusValue filter only notifies when value or quality changes.
Running can therefore show an older notification timestamp while it stays true.
Use a StatusValueTimestamp data-change filter, or read the attribute, to observe
every timestamp update. Timestamps on the server use UTC; UaExpert may display
them in the client's local timezone.

## Fault methods

Browse `Objects/Plant01/Line01/Simulation`, right-click a method and choose Call.
Method NodeIds are `ns=2;s=Line01.Simulation.<Method>`.

| Method | Inputs | Effect |
| --- | --- | --- |
| `TripPump` | None | Running becomes false and flow becomes zero on the next 1 Hz update |
| `StartPump` | None | Resume pumping on the next update |
| `SetBadQuality` | `tag: String`, `seconds: UInt32` | Report BadSensorFailure for the chosen tag, then return to Good |
| `Spike` | `tag: String` | Replace exactly one update with a value above its normal maximum |
| `RestartServer` | `delaySeconds: UInt32` | Drop every session, pause, then recreate the server in the same process |

Pass a tag as `Pump01.Vibration` or `Tank01.Temperature`, without the Line01
prefix. Bad-quality durations must be 1–86400 seconds. Spikes apply to numeric
tags; unknown tags and Boolean spikes return BadInvalidArgument.
Restart delays must be 0–5 seconds, leaving room for shutdown/startup within the
10-second recovery requirement; longer delays return BadOutOfRange. Overlapping
restarts return BadInvalidState. A restart retains model state and active
quality faults; it does not restart the container or reset the line.

## Configuration

Settings use standard .NET configuration; environment variables use double
underscores, for example `Faults__Random__Enabled=false`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `PlantSim:Endpoint` | `opc.tcp://localhost:4840` | Listening endpoint |
| `PlantSim:PkiPath` | Local application data / Telemetrix / plantsim / pki | Persistent certificate directory |
| `PlantSim:Seed` | 42 | Normal dynamics/noise seed |
| `Faults:Random:Enabled` | true | Enable optional random faults |
| `Faults:Random:Seed` | 123 | Independent random scheduling seed |
| `Faults:Random:PumpTripsPerHour` | 1 | Exponential pump-trip rate |
| `Faults:Random:PumpRestartDelay` | `00:02:00` | Automatic start after a random trip |
| `Faults:Random:VibrationSpikesPerHour` | 6 | Vibration spike rate |
| `Faults:Random:BadQualityPerHour` | 2 | Tank temperature Bad-quality rate |
| `Faults:Random:BadQualityDuration` | `00:00:30` | Duration of a random Bad-quality interval |

Rates must be finite, 0–3600 per hour; zero disables that fault. Durations must be
positive and at most one day. A seed repeats the model/schedule when driven by
the same clock steps; it does not eliminate OS scheduling variation.
Random server restarts are not scheduled; use RestartServer explicitly.
Disable random faults when checking normal ranges or a specific manual fault.

## Docker and Bridge profile

```powershell
docker compose -f deploy/docker-compose.yml up -d --build plantsim
dotnet run --no-launch-profile --project src/Telemetrix.Edge.Bridge -- --environment PlantSim
```

The PlantSim Bridge profile supplies the seven NodeIds and uses localhost:4840
for an IDE/host process. For a Bridge running on the compose network, override
`OpcUa__Endpoint=opc.tcp://plantsim:4840`. Kafka and the Transformer must also
be running to store measurements. The profile changes configuration only.

Compose disables random faults for predictable manual checks. Set
`PLANTSIM_RANDOM_FAULTS=true` to enable them. The named `plantsim-pki` volume
retains the certificate store. UaExpert uses localhost:4840 from the host;
if discovery offers the container hostname, select the discovery URL as the
connection address.

## Automated verification

```powershell
dotnet test tests/Telemetrix.PlantSim.Tests -c Release
dotnet test tests/Telemetrix.Integration.Tests -c Release --filter FullyQualifiedName~PlantSim
```

Pure unit tests use seeded randomness and an injected clock. OPC UA and
end-to-end tests live in the integration project; Kafka/database tests use
disposable Testcontainers infrastructure.
