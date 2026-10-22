# telemetry-transform Specification

## Purpose

Defines grouped consumption of raw telemetry batches, sample quality and range checks, and tag resolution with configurable treatment of unknown tags.

## Requirements

### Requirement: Consume raw batches as one group
The transformer SHALL consume `plant.telemetry.raw` as consumer group `transformer`, so running several instances splits partitions between them.

#### Scenario: Two instances
- **WHEN** two transformer instances run against the 6-partition raw topic
- **THEN** each partition is assigned to exactly one of them

### Requirement: Quality from status code
The transformer SHALL map each sample's raw OPC UA status code to a quality by its severity: Good → 0, Uncertain → 1, Bad → 2 (FR-22).

#### Scenario: Status code mapping
- **WHEN** samples arrive with status codes `0x00000000`, `0x40000000` and `0x80000000`
- **THEN** their qualities are 0, 1 and 2 respectively

### Requirement: Out-of-range values are Bad
A sample whose value lies outside its tag's valid range (`min_valid`, `max_valid`) SHALL be stored with quality Bad, regardless of its status code; a tag without a range is not range-checked (FR-22).

#### Scenario: Value above range
- **WHEN** a Good `MotorTemperature` sample has value 500 and the tag's valid range is -20…120
- **THEN** it is stored with quality 2

#### Scenario: Value missing
- **WHEN** a sample has no value
- **THEN** it is stored with quality 2 and a null value

### Requirement: Tag resolution with unknown-tag policy
The transformer SHALL resolve each sample's asset and tag names to the database tag id. For names not in the database it SHALL apply the configured policy: `dead-letter` (default) sends those samples, as a batch with the original batch id and an `unknown-tag` reason, to `plant.telemetry.dlq` and stores the rest; `create` is an explicit opt-in that inserts the tag without unit or range and stores the sample (FR-23, ADR-0011).

#### Scenario: Unknown tag, default policy
- **WHEN** a Pump01 batch contains samples for `FlowRate` and for an unknown tag `Torque`
- **THEN** the `FlowRate` samples are stored and the `Torque` samples appear on `plant.telemetry.dlq` with the original batch id and reason `unknown-tag`

#### Scenario: Unknown tag, create policy
- **WHEN** the policy is `create` and a batch contains the unknown tag `Torque`
- **THEN** a `tag` row for (`Pump01`, `Torque`) exists afterwards and its samples are stored

### Requirement: Manual offset commits

The transformer SHALL consume `plant.telemetry.raw` as a member of consumer group `transformer` with automatic offset commits disabled (FR-20).

#### Scenario: Offsets are not committed by time alone
- **WHEN** the transformer has received a batch but not yet finished processing it
- **THEN** the committed offset for that partition SHALL still point at or before that batch

### Requirement: Commit only after a durable write

The transformer MUST commit a batch's offset only after all known-tag samples have been durably written to the database and all rejected samples have been acknowledged by the dead-letter topic, or the entire invalid batch has been acknowledged by the dead-letter topic (ADR-0011). A failed write MUST NOT be committed and SHALL be retried (FR-24).

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
