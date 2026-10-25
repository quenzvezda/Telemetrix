# Proposal

## Why

Real plants often use OPC UA to talk to PLCs and MQTT to fan data out from the edge. Feeding the same tags through MQTT lets us compare the two protocols on identical data and proves the bridge's source abstraction (ADR-0006) by adding a second source without touching batching, publishing or the transformer.

Milestone: M7 — MQTT input.

## What Changes

- FR-03: a simulator publishes the 7 pumping-line tags over MQTT to `plant/Plant01/Line01/{asset}/{tag}`, with value, source timestamp and quality in each payload.
- FR-17: the bridge can collect from MQTT through the same batching, buffering and producing path as OPC UA; batches carry `source: "mqtt"`.
- The transformer is unchanged: MQTT-sourced batches are stored exactly like OPC UA ones.

## Capabilities

### New Capabilities
- `mqtt-ingestion`: the bridge collecting samples from an MQTT broker and handing them to the shared batching path.

### Modified Capabilities
- `plant-simulator`: adds MQTT publishing of the same tags (ADDED requirements extend the archived M6 capability).

## Impact

- Code: `src/Telemetrix.MqttSim` (implemented), `src/Telemetrix.Edge.Bridge/Sources` (new MQTT source), bridge configuration.
- Dependencies: MQTTnet (added to `Directory.Packages.props`).
- Infrastructure: uses the existing `mosquitto` service in `deploy/docker-compose.yml`.
- No change to the batch contract, Kafka topics or database schema.
