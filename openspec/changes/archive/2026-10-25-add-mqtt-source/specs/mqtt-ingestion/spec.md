## ADDED Requirements

### Requirement: Collect samples from MQTT
The bridge SHALL, when MQTT is the configured source, subscribe to the configured topic filter (default `plant/+/+/+/+`) and turn each valid message into a sample carrying plant, line, asset and tag taken from the topic, plus the payload's value, source timestamp and quality (FR-17).

#### Scenario: Message becomes a sample
- **WHEN** a message `{"value": 52.8, "sourceTimestamp": "2026-10-17T09:15:01Z", "quality": "good"}` arrives on `plant/Plant01/Line01/Pump01/FlowRate`
- **THEN** the bridge emits a sample for asset `Pump01`, tag `FlowRate`, value 52.8, source timestamp 2026-10-17T09:15:01Z and a Good status code

#### Scenario: Boolean payload is normalized
- **WHEN** an MQTT payload contains JSON `true` or `false`
- **THEN** the adapter emits numeric 1 or 0 respectively, preserving the numeric batch contract (ADR-0010)

#### Scenario: Quality is mapped to a status code
- **WHEN** a message arrives with `quality` set to `uncertain` or `bad`
- **THEN** the sample carries the corresponding OPC UA Uncertain or Bad status code, so the transformer assigns the same quality as for an OPC UA sample

#### Scenario: Malformed message
- **WHEN** a message has an unparsable payload or a topic that does not have five segments
- **THEN** the bridge discards it, logs a warning with the topic, counts it as rejected, and keeps consuming

### Requirement: MQTT samples share the OPC UA pipeline
Samples collected from MQTT SHALL go through the same per-asset batching, store-and-forward buffering and Kafka producing as OPC UA samples, and their batches SHALL carry `source` = `mqtt` and the broker address as `endpoint` (FR-17).

#### Scenario: Same database result from either source
- **WHEN** the bridge is switched from the OPC UA source to the MQTT source by configuration only
- **THEN** the same tags land in `measurement` with no transformer change

#### Scenario: Compare timestamp-only changes
- **WHEN** the same seven tags are sampled at 1 Hz over a shared source-time window using OPC UA and MQTT
- **THEN** both protocols retain fresh source timestamps even when a Boolean value remains constant, and their per-tag measurement counts match within a one-sample window boundary tolerance

#### Scenario: Source recorded on the batch
- **WHEN** a batch built from MQTT samples is produced to `plant.telemetry.raw`
- **THEN** its `source` field is `mqtt`

### Requirement: Pause MQTT collection at buffer capacity
The MQTT source SHALL honor the shared buffer's pause/resume thresholds, preserve accepted batches, and publish ordered connection-lost, connection-restored and gap events with reason `buffer-capacity` (ADR-0013). On resume it SHALL clear deliberately uncollected broker history, while retaining normal persistent-session recovery for network interruptions.

#### Scenario: Collection pauses and resumes
- **WHEN** the buffer reaches its pause threshold and later drains below its resume threshold
- **THEN** MQTT collection disconnects and resumes, accepted batches remain queued, old queued history from the capacity pause is cleared, and durable plant events describe the intentional collection gap

### Requirement: Recover from MQTT broker loss
The bridge SHALL reconnect to the MQTT broker with exponential backoff and resubscribe after the connection drops, and SHALL publish connection-lost and connection-restored plant events as it does for OPC UA (FR-17).

#### Scenario: Broker restarted
- **WHEN** the `mosquitto` container is restarted while the bridge is collecting
- **THEN** the bridge reconnects, resubscribes, resumes producing batches, and `plant.events` contains a connection-lost and a connection-restored event
