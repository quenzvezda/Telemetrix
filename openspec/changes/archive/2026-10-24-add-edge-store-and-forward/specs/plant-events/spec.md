## Purpose

Reports source connection transitions and collection gaps per asset so downstream users can distinguish missing collection from an unchanged plant.

## ADDED Requirements

### Requirement: Plant events topic

The bridge SHALL publish plant events to the topic `plant.events`, keyed by assetId, with 3 partitions and 30 days retention (FR-16).

#### Scenario: Topic provisioned
- **WHEN** the bridge starts in development
- **THEN** `plant.events` SHALL exist with 3 partitions and 30 days retention

### Requirement: Connection lost and restored events

The bridge SHALL publish a connection-lost event for every asset of a source when the source connection drops, and a connection-restored event when it is re-established, each with the endpoint, the UTC time of the transition and the reason when known (FR-16).

#### Scenario: OPC UA server restarted
- **WHEN** the OPC UA session drops and is later re-established
- **THEN** a connection-lost event and then a connection-restored event SHALL be published for each affected asset

### Requirement: Gap events

After a connection is restored the bridge SHALL publish a gap event per affected asset stating the start and end (UTC) of the period without samples. The bridge MUST NOT fabricate samples for the gap.

#### Scenario: Gap after reconnect
- **WHEN** the connection is restored after an interruption
- **THEN** a gap event SHALL be published whose start is the time of the last sample before the drop and whose end is the time of the first sample after reconnection
- **AND** no measurement SHALL be stored for timestamps inside the gap

#### Scenario: Collection paused at buffer capacity
- **WHEN** collection is paused and later resumed because the edge buffer reached its capacity threshold
- **THEN** lost, restored and gap events SHALL identify `buffer-capacity` as their reason
- **AND** the gap bounds SHALL use the last collected and first resumed source timestamps

### Requirement: Events are not lost during a Kafka outage

Plant events SHALL go through the same store-and-forward path as batches, so they are not lost when Kafka is unreachable.

#### Scenario: Event raised while Kafka is down
- **WHEN** a connection-lost event occurs while Kafka is unreachable
- **THEN** it SHALL be produced to `plant.events` after Kafka returns
