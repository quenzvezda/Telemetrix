## ADDED Requirements

### Requirement: Spill batches when Kafka is unreachable

The bridge SHALL write a batch to a local on-disk buffer instead of dropping it whenever the batch cannot be acknowledged by Kafka, and SHALL keep collecting samples while Kafka is unreachable (FR-15).

#### Scenario: Kafka stopped
- **WHEN** Kafka becomes unreachable while the bridge is collecting
- **THEN** every subsequent batch SHALL be persisted to the local buffer
- **AND** the OPC UA subscription SHALL keep delivering samples

### Requirement: Replay in original order

When Kafka is reachable again the bridge SHALL produce buffered batches in the order they were created, and SHALL NOT produce a newer batch for an asset before all older buffered batches for that asset have been acknowledged (FR-15, NFR-05).

#### Scenario: Recovery after outage
- **WHEN** Kafka returns after an outage during which batches were buffered
- **THEN** the buffered batches SHALL be produced in creation order
- **AND** a batch SHALL be removed from the buffer only after Kafka acknowledges it

#### Scenario: Live batches during drain
- **WHEN** new batches are created while the buffer is still draining
- **THEN** those new batches SHALL be produced only after the older buffered batches of the same asset

### Requirement: Buffer survives restart

The buffered batches SHALL survive a bridge process restart and SHALL be replayed after the restart.

#### Scenario: Bridge restarted during outage
- **WHEN** the bridge is restarted while batches are buffered and Kafka is still down
- **THEN** after Kafka returns, all batches buffered before the restart SHALL be produced exactly once each to `plant.telemetry.raw`

### Requirement: Zero loss over a 30 minute outage

A Kafka outage of 30 minutes at the default sample rate SHALL result in zero lost samples once the buffer has drained (NFR-02).

#### Scenario: 30 minute outage
- **WHEN** Kafka is stopped for 30 minutes and then started
- **THEN** after the buffer drains, the number of stored measurements SHALL equal the number of samples the simulator produced in that period

### Requirement: Observable buffer depth

The bridge SHALL expose the current number of buffered batches and samples as a metric and log a warning when buffering starts and an information message when the buffer is empty again.

#### Scenario: Buffer depth reported
- **WHEN** batches are buffered
- **THEN** the buffer depth metric SHALL equal the number of batches waiting to be produced
