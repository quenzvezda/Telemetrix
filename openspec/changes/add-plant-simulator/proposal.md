# Proposal

## Why

OPC PLC exposes generic sine waves and ramps that the bridge must map onto our tag names, and it cannot reproduce the plant behaviour the pipeline is meant to handle: correlated values, pump trips, Bad quality, server restarts. Writing our own server models the pumping line faithfully and teaches the server side of OPC UA's information model.

Milestone: M6 — Own OPC UA server (`PlantSim`).

## What Changes

- New OPC UA server `PlantSim` exposing the 7 tags of the pumping line under `Objects/Plant01/Line01`, namespace index 2, with the NodeIds from the data model, updating at 1 Hz with plausible noise and correlations (FR-01).
- Fault injection on demand and at random: pump trip, Bad-quality status, value spike, server restart (FR-02).
- `plantsim` service in `deploy/docker-compose.yml`; the bridge switches from OPC PLC to PlantSim by configuration only.

## Capabilities

### New Capabilities

- `plant-simulator`: the simulated plant's OPC UA address space, value dynamics and fault injection.

### Modified Capabilities

None.

## Impact

- `src/Telemetrix.PlantSim` (currently an empty worker template).
- New dependency: OPCFoundation.NetStandard.Opc.Ua.Server.
- `deploy/docker-compose.yml` (new `plantsim` service, port 4840), a Dockerfile for PlantSim.
- Bridge configuration gains a PlantSim profile; no bridge code change.
- New test project `tests/Telemetrix.PlantSim.Tests` for the value model.
