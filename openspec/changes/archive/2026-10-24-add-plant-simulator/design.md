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

`Model/PumpingLine` is a plain class: `Step(TimeSpan dt, Random rng) → IReadOnlyList<TagValue>` holding state (running, flow, temperature, level…). The worker calls the node manager once per second from a `PeriodicTimer` using injected `TimeProvider`. It writes values, status codes and UTC source timestamps (from the provider, truncated to the second) into the nodes, then `ClearChangeMasks`. This makes the physics and timed quality faults unit-testable without a server.

Dynamics (first-order lags with Gaussian noise): flow approaches 50 m³/h when running and is 0 when stopped; pressure = 1.0 + 0.075·flow + noise; motor temperature approaches 45 + 0.6·flow °C with a time constant of ~10 min; level integrates inflow minus outflow. Normal operation starts in range and clamps numeric values to the spec's normal ranges, including level 20–90 %. Stopped pressure is 1 bar. Vibration follows flow with noise; tank temperature is a 15–30 °C slow random walk. These bounds refine the initial design's wider 0–100 % level clamp to satisfy the unchanged normal-range requirement.

Source timestamps are updated for all tags even if their values are unchanged.
Default OPC UA StatusValue data-change filters suppress timestamp-only
notifications. Clients checking the 1 Hz timestamp requirement use
StatusValueTimestamp or read the attributes directly; the Bridge still switches
source through configuration only.

### Fault injection: OPC UA methods + random scheduler

A `Simulation` object under `Line01` exposes methods: `TripPump()`, `StartPump()`, `SetBadQuality(tag: String, seconds: UInt32)`, `Spike(tag: String)`, `RestartServer(delaySeconds: UInt32)`. Methods are callable from UaExpert and from integration tests via the client SDK.

- Alternative: HTTP control endpoint. Rejected — methods keep the exercise inside OPC UA and are the same mechanism the "write-back" stretch goal needs.
- `RestartServer` queues a worker-owned restart, lets its Call response leave, stops the `ApplicationInstance` (dropping all sessions), waits the delay, and starts a new server in-process; the container keeps running. Delays are bounded to 0–5 seconds and the SDK shutdown countdown is disabled to keep the complete restart within 10 seconds. Concurrent restarts are rejected. Model state and active quality faults remain in the shared model.

`RandomFaultScheduler` (BackgroundService) draws Poisson-distributed events from `appsettings.json` (`Faults:Random:Enabled`, `PumpTripsPerHour=1`, `PumpRestartDelay=00:02:00`, `VibrationSpikesPerHour=6`, `BadQualityPerHour=2`, `Seed`).

### Deployment

`src/Telemetrix.PlantSim/Dockerfile` (multi-stage, `mcr.microsoft.com/dotnet/runtime:10.0`), compose service `plantsim` on port 4840 with a named volume for its `pki/` store and the non-root app user. The `appsettings.PlantSim.json` Bridge profile uses localhost:4840 for IDE/host runs and the seven NodeIds; a container-side endpoint is overridden to `opc.tcp://plantsim:4840`. Compose disables random faults for repeatable manual checks; `PLANTSIM_RANDOM_FAULTS=true` enables them.

## Risks / Trade-offs

- [Namespace index is not guaranteed to be 2 if another namespace is registered first] → register only one custom namespace; an integration test asserts index 2.
- [Server SDK API churn between versions] → pin the package version in `Directory.Packages.props`; follow the SDK's reference server sample.
- [Truncated source timestamps collide with `UNIQUE (tag_id, time)` if two updates land in the same tick] → one update per tag per second by construction; spikes replace, not add, an update.
