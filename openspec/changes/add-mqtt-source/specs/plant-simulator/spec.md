## ADDED Requirements

### Requirement: Publish tags over MQTT
The simulator SHALL publish every tag of the simulated pumping line to an MQTT broker on topic `plant/{plantId}/{lineId}/{assetId}/{tag}` (e.g. `plant/Plant01/Line01/Pump01/FlowRate`) at the same 1 Hz rate as the OPC UA simulator (FR-03).

#### Scenario: Every tag is published once per second
- **WHEN** the MQTT simulator runs for 10 seconds against a reachable broker
- **THEN** each of the 7 tag topics under `plant/Plant01/Line01/` receives about 10 messages

#### Scenario: Broker unavailable at start
- **WHEN** the broker is unreachable when the simulator starts
- **THEN** the simulator retries the connection and starts publishing once the broker is reachable, without exiting

### Requirement: MQTT payload carries value, source timestamp and quality
Each MQTT message SHALL be a JSON object with the tag's value, its source timestamp in UTC (ISO 8601), and a quality of `good`, `uncertain` or `bad`, so a subscriber can build a sample without any other context (FR-03).

#### Scenario: Numeric tag payload
- **WHEN** `Pump01/MotorTemperature` is published
- **THEN** the payload contains a numeric `value`, a UTC `sourceTimestamp`, and `quality` set to `good`

#### Scenario: Boolean tag payload
- **WHEN** `Pump01/Running` is published
- **THEN** the payload `value` is the JSON literal `true` or `false`

#### Scenario: Simulated bad quality
- **WHEN** the simulator reports `Tank01/Temperature` as unreliable
- **THEN** the payload `quality` is `bad`
