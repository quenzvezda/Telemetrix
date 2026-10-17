# Design

## Context

`src/Telemetrix.PlantSim` is an empty worker template. The bridge (M2–M5) reads tags by NodeId from configuration and already handles reconnects and Bad status codes. See proposal.md for motivation.

## Goals / Non-Goals

**Goals:**
- Faithful, deterministic-when-seeded model of the pumping line.
- Faults triggerable from UaExpert, a script, or random scheduling.

**Non-Goals:**
- Cloning lines for load (done in M8 for NFR-07; the model is written so a line can be instantiated N times).
- OPC UA security (M9); PlantSim starts with SecurityMode None like OPC PLC.
- MQTT publishing (M7, `MqttSim`).

## Decisions

### Server: OPC Foundation .NET Standard server SDK

`ApplicationInstance` + `StandardServer` subclass + one `CustomNodeManager2` (`PlantNodeManager`) owning namespace `urn:telemetrix:plantsim` (index 2 because it is the first custom namespace). Objects `Plant01 → Line01 → Pump01/Tank01` are `FolderType`/`BaseObjectType`; variables are `BaseDataVariableState` with string NodeIds `Line01.<Asset>.<Tag>` and `EngineeringUnits` properties. Endpoint `opc.tcp://0.0.0.0:4840`, auto-accept untrusted client certs in development.

### Value model separated from OPC UA

`Model/PumpingLine` is a plain class: `Step(TimeSpan dt, Random rng) → IReadOnlyList<TagValue>` holding state (running, flow, temperature, level…). The node manager calls it once per second from a `PeriodicTimer` and writes values, status codes and source timestamps (`DateTime.UtcNow` truncated to the tick) into the nodes, then `ClearChangeMasks`. This makes the physics unit-testable without a server (seeded `Random`).

Dynamics (first-order lags with Gaussian noise): flow → target 50 m³/h when running, 0 when stopped; pressure = 1.0 + 0.075·flow + noise; motor temperature approaches 45 + 0.6·flow °C with a time constant of ~10 min; level integrates (inflow when running − constant outflow) clamped 0–100; tank temperature 15–30 °C slow random walk.

### Fault injection: OPC UA methods + random scheduler

A `Simulation` object under `Line01` exposes methods: `TripPump()`, `StartPump()`, `SetBadQuality(tag: String, seconds: UInt32)`, `Spike(tag: String)`, `RestartServer(delaySeconds: UInt32)`. Methods are callable from UaExpert and from integration tests via the client SDK.

- Alternative: HTTP control endpoint. Rejected — methods keep the exercise inside OPC UA and are the same mechanism the "write-back" stretch goal needs.
- `RestartServer` stops the `ApplicationInstance` (dropping all sessions), waits the delay, and starts it again in-process; the container keeps running.

`RandomFaultScheduler` (BackgroundService) draws Poisson-distributed events from `appsettings.json` (`Faults:Random:Enabled`, `PumpTripsPerHour=1`, `PumpRestartDelay=00:02:00`, `VibrationSpikesPerHour=6`, `BadQualityPerHour=2`, `Seed`).

### Deployment

`src/Telemetrix.PlantSim/Dockerfile` (multi-stage, `mcr.microsoft.com/dotnet/runtime:10.0`), compose service `plantsim` on port 4840 with a named volume for its `pki/` store. A bridge config profile `appsettings.PlantSim.json` (or env vars) sets endpoint `opc.tcp://plantsim:4840` and the 7 NodeIds.

## Risks / Trade-offs

- [Namespace index is not guaranteed to be 2 if another namespace is registered first] → register only one custom namespace; an integration test asserts index 2.
- [Server SDK API churn between versions] → pin the package version in `Directory.Packages.props`; follow the SDK's reference server sample.
- [Truncated source timestamps collide with `UNIQUE (tag_id, time)` if two updates land in the same tick] → one update per tag per second by construction; spikes replace, not add, an update.
