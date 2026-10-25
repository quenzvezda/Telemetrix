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

### Requirement: One-minute rollups
The database SHALL maintain, per tag and per 1-minute bucket, the minimum, maximum, average and count of Good measurements, refreshed automatically so that every completed minute older than a few minutes has its rollup (FR-31).

#### Scenario: Rollup reflects good data only
- **WHEN** a tag has three Good measurements (10, 20, 30) and one Bad measurement (999) in the same minute, and the refresh has run
- **THEN** that tag's rollup for the minute has min 10, max 30, avg 20 and count 3

#### Scenario: Rollup catches up with recent data
- **WHEN** live data has been flowing for 10 minutes
- **THEN** rollups exist for every completed minute older than the refresh lag

### Requirement: Historical rollup backfill
The rollup migration SHALL populate completed historical buckets from retained raw measurements, including data from earlier milestones, and reruns SHALL preserve older rollups after their raw history has expired (FR-31, FR-32).

#### Scenario: Existing historical measurements
- **WHEN** the migration runs on a database containing Good measurements from two days earlier
- **THEN** the corresponding completed one-minute buckets contain rollups without waiting for the rolling refresh policy

#### Scenario: Rerun after raw retention
- **WHEN** older rollups remain after their raw measurements expire and the migration is rerun with newer raw data present
- **THEN** the older rollups remain and completed buckets in the retained raw window are refreshed

### Requirement: Retention
The database SHALL drop raw measurements older than 30 days and rollups older than 1 year automatically (FR-32).

#### Scenario: Old raw data is removed
- **WHEN** the retention job runs while `measurement` holds data older than 30 days
- **THEN** that raw data is removed and its rollups remain

#### Scenario: Old rollups are removed
- **WHEN** the retention job runs while the rollups hold buckets older than 1 year
- **THEN** those buckets are removed

### Requirement: Schema changes apply to existing databases
Schema changes after the initial schema SHALL be provided as reviewable SQL files under `deploy/sql/migrations/` and applied in order, idempotently, to both a fresh and an already-initialised database without losing stored measurements (FR-31, FR-32). Applying migrations to the local database SHALL be an explicit reviewer action after SQL review; default stack startup SHALL NOT apply migrations. Automated migration checks SHALL use disposable test databases.

#### Scenario: Existing volume
- **WHEN** the migration runs against a database created by an earlier milestone
- **THEN** the rollups and retention policies exist and all previously stored measurements remain

#### Scenario: Re-run
- **WHEN** the same migration runs twice
- **THEN** the second run succeeds and changes nothing

#### Scenario: Default startup preserves review boundary
- **WHEN** the stack starts with its default Compose profiles
- **THEN** the migration service does not run; the reviewer can apply the reviewed SQL using the explicit migration command
