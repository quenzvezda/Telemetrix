# Design

## Context

The bridge already reads OPC UA through a source-adapter interface that emits samples into the batching channel (ADR-0006). `src/Telemetrix.MqttSim` is an empty console project, and `deploy/docker-compose.yml` already runs Mosquitto on port 1883 with anonymous access for development. MQTT has no built-in status codes, types or source timestamps, so this change has to define a payload convention.

## Goals / Non-Goals

**Goals:**
- One payload convention shared by MqttSim and the bridge, defined in `Telemetrix.Contracts`.
- `MqttSourceAdapter` behind the existing `ISourceAdapter`, selected by configuration.

**Non-Goals:**
- Sparkplug B (stretch goal).
- MQTT TLS or authentication (M9 covers OPC UA security only; Mosquitto stays dev-only).
- Running OPC UA and MQTT sources in the same bridge instance at the same time.

## Decisions

### Payload shape
```json
{ "value": 52.8, "sourceTimestamp": "2026-10-17T09:15:01.000Z", "quality": "good" }
```
- Record `MqttTagPayload(JsonElement Value, DateTimeOffset SourceTimestamp, string Quality)` in `Telemetrix.Contracts`, using the shared `JsonSerializerOptions`.
- MQTT booleans travel as JSON `true`/`false`; the adapter converts them to numeric 1/0 before emitting `Sample`, as required by ADR-0010. Batches remain numeric.
- Plant/line/asset/tag come from the topic, not the payload, so the topic is the single identity (mirrors the NodeId in OPC UA).
- Alternative: one message per asset with all tags — rejected; per-tag topics are the common convention and make retained "last value" per tag work.

### Quality → status code
The adapter maps `good` → `0x00000000` (Good), `uncertain` → `0x40000000` (Uncertain), `bad` → `0x80000000` (Bad). The transformer's existing status-code mapping then produces the same quality as for OPC UA, so no transformer change is needed. Unknown quality strings are treated as Bad and logged.

### Timestamps
`sourceTimestamp` from the payload is the sample's source timestamp (ADR-0009). `serverTimestamp` is set to the bridge's receive time (UTC) so clock drift between simulator and bridge stays diagnosable.

### QoS
- MqttSim publishes with QoS 1 (at-least-once) and `retain = true`, so a newly connected subscriber gets the last value of every tag immediately.
- The bridge subscribes with QoS 1 and a persistent session (`CleanSession = false`, fixed client id) so short bridge restarts do not lose messages queued at the broker. Duplicates from QoS 1 are harmless because the database write is idempotent on `(tag_id, time)` (ADR-0003).
- Alternative: QoS 2 — rejected; exactly-once at the MQTT hop buys nothing when the end-to-end path is already idempotent, and costs an extra round trip per message.
- Retained messages delivered on (re)subscribe carry their original source timestamp, so they deduplicate in the database.

### Library and wiring
- MQTTnet (client only) in `Telemetrix.MqttSim` and `Telemetrix.Edge.Bridge`.
- `Sources/MqttSourceAdapter.cs` implements `ISourceAdapter`; configuration section `Source:Kind` = `OpcUa` | `Mqtt`, plus `Source:Mqtt:Host`, `Port`, `TopicFilter` (default `plant/+/+/+/+`), `ClientId`.
- Reconnect uses the existing capped exponential backoff with jitter and durable plant-event publishing. The current OPC UA adapter does not use Polly.
- Keep MqttSim as a separate process, as accepted by the reviewer on 2026-10-25. Extract the protocol-independent pumping-line model and tag definitions into `Telemetrix.Simulation`; both simulators reference it. Preserve PlantSim behavior and seeded tests.
- MQTTnet 5 client uses MQTT 3.1.1 explicitly. Simulator publish retries preserve the payload and source timestamp until acknowledged; no process-crash durability is promised at the simulator.
- At buffer capacity, disconnect collection and report `buffer-capacity` through the existing durable event path. On resumption, clear the broker session once before restoring the persistent subscription so deliberately uncollected changes are not replayed as recovered history. Retained values keep their source timestamps. Normal network reconnects preserve the session and queued QoS 1 deliveries. Broker queues are finite; persistent sessions are not an infinite-lossless guarantee.
- Track events per plant/line/asset discovered in topics; serialize event transitions and sample admission. A normal MQTT reconnect reports connection events; replayed history does not itself establish missing measurements. Capacity gaps follow ADR-0013.
- MqttSim can run in Docker through a compose profile; MQTT Bridge settings are supplied through a configuration profile without changing Transformer.

## Risks / Trade-offs

- [Simulator models diverge] → share the line model between PlantSim and MqttSim instead of duplicating it.
- [Retained messages replay stale values after a long outage] → they keep their original source timestamp; the dashboard's gap detection uses source time, so no fake fresh data appears.
- [Topic with unexpected segment count floods logs] → rate-limit the malformed-message warning and expose a rejected-message counter.

## Open Questions

None blocking M7. The reviewer accepted a separate MqttSim process and authorized all task groups autonomously on 2026-10-25. Archive and milestone acceptance remain pending PR review.
