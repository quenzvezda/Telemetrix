## ADDED Requirements

### Requirement: Durable idempotent producer

The bridge SHALL produce raw batches with acknowledgement from all in-sync replicas and with producer idempotence enabled, so a producer retry neither loses nor duplicates nor reorders a batch within a partition (NFR-08).

#### Scenario: Producer configuration
- **WHEN** the bridge starts its Kafka producer
- **THEN** the producer SHALL require acknowledgement from all in-sync replicas
- **AND** producer idempotence SHALL be enabled

#### Scenario: Transient broker error
- **WHEN** a produce request fails with a retriable error and is retried
- **THEN** the batch SHALL appear exactly once in `plant.telemetry.raw`
- **AND** its order relative to other batches with the same key SHALL be preserved
