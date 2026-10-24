# Proposal

## Why

The bridge is the only component that sees the plant; if it loses Kafka or its OPC UA session, every sample in that window is gone for good. Kafka outages must be bridged by a local buffer, and lost OPC UA sessions must be recovered automatically and reported, so downstream consumers can tell "no data" from "nothing happened".

Milestone: M5 — Edge resilience.

## What Changes

- Bridge spills batches to a local on-disk buffer when Kafka is unreachable and replays them in original order when it returns, surviving bridge restarts (FR-15, NFR-02).
- Bridge reconnects to the OPC UA server with exponential backoff and restores its subscriptions within 30 s of the server becoming available (FR-16, NFR-04).
- Bridge publishes connection-lost, connection-restored and gap events to `plant.events`, keyed by assetId (FR-16).
- Buffer depth is observable (metric and log), feeding FR-41 in M8.
- D3 is resolved in ADR-0013: configurable capacity for at least one hour at default load; pause collection before overflow, retain accepted batches and report a gap.

## Capabilities

### New Capabilities

- `edge-buffering`: store-and-forward of batches at the edge while the backbone is unreachable.
- `plant-events`: notices about collection itself (connection lost/restored, gaps) on `plant.events`.

### Modified Capabilities

- `opcua-ingestion`: adds bounded exponential reconnect and collection pause/resume to the existing OPC UA source. Earlier milestones are archived; these requirements are additional behavior.

## Impact

- `Telemetrix.Edge.Bridge` (Buffer, Publishing, Sources), `Telemetrix.Contracts` (`PlantEvent` record).
- New dependency: Microsoft.Data.Sqlite; local buffer file (git-ignored `*.db`).
- New topic `plant.events` (3 partitions, 30 days), created by the bridge at startup (ADR-0007).
- `tests/Telemetrix.Edge.Bridge.Tests`, `tests/Telemetrix.Integration.Tests`.
