## ADDED Requirements

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
Each message value SHALL be a JSON batch with `schemaVersion` (1), a unique `batchId` (UUID), `source`, `endpoint`, `plantId`, `lineId`, `assetId`, `createdAt` and a `samples` array whose items carry `tag`, `value`, `sourceTimestamp`, `serverTimestamp` and the raw `statusCode`. Timestamps MUST be ISO-8601 UTC; booleans MUST travel as 1/0 (ADR-0008).

#### Scenario: Message matches the contract
- **WHEN** the bridge produces a batch
- **THEN** the message deserialises into the shared batch contract with `schemaVersion` 1 and every timestamp ending in `Z`

#### Scenario: Batch ids are unique
- **WHEN** the bridge produces many batches, including after a restart
- **THEN** no two batches share a `batchId`
