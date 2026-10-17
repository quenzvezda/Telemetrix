# Proposal

## Why

The bridge is the only component that sees the plant; if it loses Kafka or its OPC UA session, every sample in that window is gone for good. Kafka outages must be bridged by a local buffer, and lost OPC UA sessions must be recovered automatically and reported, so downstream consumers can tell "no data" from "nothing happened".

Milestone: M5 — Edge resilience.

## What Changes

- Bridge spills batches to a local on-disk buffer when Kafka is unreachable and replays them in original order when it returns, surviving bridge restarts (FR-15, NFR-02).
- Bridge reconnects to the OPC UA server with exponential backoff and restores its subscriptions within 30 s of the server becoming available (FR-16, NFR-04).
- Bridge publishes connection-lost, connection-restored and gap events to `plant.events`, keyed by assetId (FR-16).
- Buffer depth is observable (metric and log), feeding FR-41 in M8.
- Blocked on open decision D3 (buffer size limit and overflow policy), to be resolved and recorded as an ADR before implementation.

## Capabilities

### New Capabilities

- `edge-buffering`: store-and-forward of batches at the edge while the backbone is unreachable.
- `plant-events`: notices about collection itself (connection lost/restored, gaps) on `plant.events`.
- `opcua-ingestion`: adds automatic reconnection and subscription recovery to the OPC UA source introduced in M2.

### Modified Capabilities

None (earlier changes are not archived yet; requirements are added as new).

## Impact

- `Telemetrix.Edge.Bridge` (Buffer, Publishing, Sources), `Telemetrix.Contracts` (`PlantEvent` record).
- New dependency: Microsoft.Data.Sqlite; local buffer file (git-ignored `*.db`).
- New topic `plant.events` (3 partitions, 30 days), created by the bridge at startup (ADR-0007).
- `tests/Telemetrix.Edge.Bridge.Tests`, `tests/Telemetrix.Integration.Tests`.
