## ADDED Requirements

### Requirement: Manual offset commits

The transformer SHALL consume `plant.telemetry.raw` as a member of consumer group `transformer` with automatic offset commits disabled (FR-20).

#### Scenario: Offsets are not committed by time alone
- **WHEN** the transformer has received a batch but not yet finished processing it
- **THEN** the committed offset for that partition SHALL still point at or before that batch

### Requirement: Commit only after a durable write

The transformer MUST commit a batch's offset only after every measurement of that batch has been durably written to the database or the batch has been dead-lettered. A failed write MUST NOT be committed and SHALL be retried (FR-24).

#### Scenario: Write succeeds
- **WHEN** a batch's measurements are written successfully
- **THEN** the transformer SHALL commit the offset following that batch

#### Scenario: Database unavailable
- **WHEN** the database write for a batch fails
- **THEN** the transformer SHALL NOT commit the offset
- **AND** SHALL retry the same batch until the write succeeds or the transformer stops

#### Scenario: Transformer killed mid-batch
- **WHEN** the transformer process is killed after writing a batch but before committing it, and is restarted
- **THEN** the batch SHALL be consumed again
- **AND** the `measurement` table SHALL contain no duplicate and no missing rows for that batch (NFR-03)

### Requirement: Dead-letter invalid batches

The transformer SHALL publish any batch that fails schema validation (unparseable JSON, missing required fields, unsupported `schemaVersion`) to `plant.telemetry.dlq` with the original key and payload, and an error-reason header, then continue with the next batch (FR-21).

#### Scenario: Malformed payload
- **WHEN** a message on `plant.telemetry.raw` is not valid JSON
- **THEN** the transformer SHALL publish it unchanged to `plant.telemetry.dlq` with the original key
- **AND** the message SHALL carry a header describing the validation error
- **AND** the transformer SHALL commit past it and process the next message

#### Scenario: Unsupported schema version
- **WHEN** a batch declares a `schemaVersion` the transformer does not support
- **THEN** the batch SHALL be dead-lettered with a reason naming the unsupported version
- **AND** no measurement from it SHALL be written

### Requirement: Exactly-once storage of delivered samples

Every sample that reaches the bridge SHALL be stored exactly once in `measurement`, even when batches are delivered more than once (NFR-01).

#### Scenario: Replayed batch
- **WHEN** the same batch is consumed twice
- **THEN** the number of rows in `measurement` SHALL be the same as after the first consumption

### Requirement: Per-asset ordering

The transformer SHALL process the samples of one asset in source-timestamp order (NFR-05).

#### Scenario: Consecutive batches of one asset
- **WHEN** two batches for the same asset are produced one after the other
- **THEN** the transformer SHALL write the earlier batch before the later one
- **AND** within the stored data, source timestamps per tag SHALL be monotonically non-decreasing in processing order
