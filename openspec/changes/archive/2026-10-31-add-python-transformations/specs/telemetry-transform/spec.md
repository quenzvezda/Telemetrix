## MODIFIED Requirements

### Requirement: Commit only after a durable write

The transformer MUST commit a batch's offset only after all known-tag samples have been durably written to the database, together with the batch's transformation run record, derived measurements and dispositions when a transformation applies, and all rejected samples have been acknowledged by the dead-letter topic, or the entire invalid batch has been acknowledged by the dead-letter topic (ADR-0011, ADR-0018). A failed write MUST NOT be committed and SHALL be retried (FR-24).

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

#### Scenario: Transformation recorded atomically
- **WHEN** a labeled batch is transformed and its write transaction fails
- **THEN** neither its raw measurements nor its derived measurements nor its run record are stored, and the offset is not committed

### Requirement: Dead-letter invalid batches

The transformer SHALL publish any batch that fails schema validation (unparseable JSON, missing required fields, unsupported `schemaVersion`, a `transformation` field on a version 1 batch, or an invalid transformation label) to `plant.telemetry.dlq` with the original key and payload, and an error-reason header, then continue with the next batch (FR-21). Schema versions 1 and 2 are supported (ADR-0017).

#### Scenario: Malformed payload
- **WHEN** a message on `plant.telemetry.raw` is not valid JSON
- **THEN** the transformer SHALL publish it unchanged to `plant.telemetry.dlq` with the original key
- **AND** the message SHALL carry a header describing the validation error
- **AND** the transformer SHALL commit past it and process the next message

#### Scenario: Unsupported schema version
- **WHEN** a batch declares a `schemaVersion` the transformer does not support
- **THEN** the batch SHALL be dead-lettered with a reason naming the unsupported version
- **AND** no measurement from it SHALL be written

#### Scenario: Invalid transformation label
- **WHEN** a version 2 batch carries the transformation `../evil.py`
- **THEN** the batch SHALL be dead-lettered with reason `schema-violation`
- **AND** no measurement from it SHALL be written
