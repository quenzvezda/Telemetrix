# telemetry-publishing Specification

## Purpose

Defines the Bridge-owned raw telemetry topic, asset-based message keys and the versioned batch contract shared by producers and consumers.

## Requirements

### Requirement: Raw topic owned by the bridge
The bridge SHALL ensure `plant.telemetry.raw` exists at startup with 6 partitions and 7-day retention, creating it if missing and leaving an existing topic unchanged (ADR-0007).

#### Scenario: First start
- **WHEN** the bridge starts against a broker without `plant.telemetry.raw`
- **THEN** the topic exists afterwards with 6 partitions and `retention.ms` of 7 days

### Requirement: Batch keyed by asset
Every batch SHALL be produced to `plant.telemetry.raw` with the asset id as the message key, so all batches of one asset land in the same partition (FR-14, ADR-0004).

#### Scenario: Same partition per asset
- **WHEN** the bridge produces ten Pump01 batches
- **THEN** all ten carry key `Pump01` and are in the same partition

### Requirement: Versioned batch message
Each message value SHALL be a JSON batch with `schemaVersion` (1), a unique `batchId` (UUID), `source`, `endpoint`, `plantId`, `lineId`, `assetId`, `createdAt` and a `samples` array whose items carry `tag`, `value`, `sourceTimestamp`, `serverTimestamp` and the raw `statusCode`. Timestamps MUST be ISO-8601 UTC; values SHALL be numbers or null, with booleans converted to 1/0 by source adapters before batching and strings out of scope for v1 (ADR-0008, ADR-0010).

#### Scenario: Message matches the contract
- **WHEN** the bridge produces a batch
- **THEN** the message deserialises into the shared batch contract with `schemaVersion` 1 and every timestamp ending in `Z`

#### Scenario: Batch ids are unique
- **WHEN** the bridge produces many batches, including after a restart
- **THEN** no two batches share a `batchId`

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
