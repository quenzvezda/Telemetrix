# telemetry-storage Specification

## Purpose

Defines bulk storage of measurements with UTC source timestamps, stable sample identity and all-or-nothing writes that remain idempotent on replay.

## Requirements

### Requirement: One measurement per tag and source timestamp
Each stored sample SHALL become one row in the `measurement` hypertable with `time` = the sample's source timestamp (UTC), its tag id, value, quality, the batch id and the ingest time; a second sample for the same tag and source timestamp MUST NOT create a second row (FR-30, ADR-0003, ADR-0009).

#### Scenario: Batch stored
- **WHEN** a batch with 35 samples for 7 tags is processed
- **THEN** 35 rows exist in `measurement` carrying that batch id

#### Scenario: Replayed batch
- **WHEN** the same batch is processed twice
- **THEN** the row count is unchanged after the second pass and no error is raised

### Requirement: Bulk write
The transformer SHALL write each batch's samples to the database in a single bulk operation per batch, not row by row.

#### Scenario: Partial failure is all-or-nothing
- **WHEN** the database write of a batch fails midway
- **THEN** none of that batch's samples are visible in `measurement`
