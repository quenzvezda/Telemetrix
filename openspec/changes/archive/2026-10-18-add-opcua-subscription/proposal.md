# Proposal

## Why

OPC UA is how the plant talks (ADR-0006), and subscriptions, status codes and source timestamps are the part of the pipeline most worth learning. Proving the client against OPC PLC before anything else keeps M3 focused on Kafka and the database.

## What Changes

Milestone: M2 — OPC UA client.

- The bridge loads the OPC UA endpoint, security mode and tag list from configuration, overridable by environment variables (FR-10).
- It creates one subscription with one monitored item per configured tag, with configurable sampling interval, publishing interval and queue size (FR-11).
- Optionally it discovers tags by browsing a start node instead of listing them (FR-12, Should).
- Each data-change notification becomes a sample (asset, tag, value, source timestamp, server timestamp, status code) and is logged; nothing is sent to Kafka yet.
- After the server restarts, the bridge resumes delivering samples without being restarted itself. Backoff tuning and connection events come in M5.

## Capabilities

### New Capabilities

- `opcua-ingestion`: how the bridge collects samples from an OPC UA server — configuration, subscription, sample shape, session recovery.

### Modified Capabilities

None.

## Impact

- `src/Telemetrix.Contracts` (sample record), `src/Telemetrix.Edge.Bridge/Sources`, bridge `appsettings*.json`.
- New package: `OPCFoundation.NetStandard.Opc.Ua.Client`.
- `tests/Telemetrix.Edge.Bridge.Tests`.
