## ADDED Requirements

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
