# telemetry-batching Specification

## Purpose

Defines per-asset sample batching by size or age, preserving arrival order and flushing pending samples during graceful Bridge shutdown.

## Requirements

### Requirement: One batch per asset
The bridge SHALL group samples into batches that each contain samples of exactly one asset, preserving the order in which the samples arrived (FR-13).

#### Scenario: Mixed assets
- **WHEN** samples for Pump01 and Tank01 arrive interleaved
- **THEN** the bridge emits separate batches for Pump01 and Tank01, and each batch lists its samples in arrival order

### Requirement: Size or time flush
An open batch SHALL be flushed when it reaches the configured sample count (default 500) or when the configured age elapses since its first sample (default 5 s), whichever comes first (FR-13).

#### Scenario: Size threshold
- **WHEN** an asset's open batch reaches 500 samples within 2 s
- **THEN** that batch is flushed immediately with 500 samples and a new batch starts with the next sample

#### Scenario: Time threshold
- **WHEN** an asset produces 7 samples per second
- **THEN** its batch is flushed 5 s after its first sample with about 35 samples

#### Scenario: Quiet asset
- **WHEN** no sample arrives for an asset
- **THEN** no empty batch is emitted for it

### Requirement: Flush on shutdown
On graceful shutdown the bridge SHALL flush every open batch before exiting.

#### Scenario: Stop during an open batch
- **WHEN** the bridge is stopped while Pump01 has 120 unflushed samples
- **THEN** a Pump01 batch with those 120 samples is handed to publishing before the process exits

### Requirement: Transformation label per Asset
The bridge SHALL assign the transformation label configured for an Asset (`Transformations:Assets:<AssetId>`) to every Batch of that Asset, emitting `schemaVersion` 2 with a `transformation` field. Batches of Assets without a label SHALL be emitted as `schemaVersion` 1 without the field. An invalid label SHALL fail bridge startup (FR-26, ADR-0017).

#### Scenario: Two Assets, two labels
- **WHEN** Pump01 is assigned `pump-cleaning` and Tank01 `celsius-to-fahrenheit`
- **THEN** Pump01 Batches carry `"schemaVersion": 2, "transformation": "pump-cleaning"` and Tank01 Batches carry `celsius-to-fahrenheit`

#### Scenario: Unlabeled Asset
- **WHEN** an Asset has no label or an empty label
- **THEN** its Batches are `schemaVersion` 1 and contain no `transformation` field

#### Scenario: Invalid label
- **WHEN** an Asset is assigned `../evil.py`
- **THEN** the bridge fails to start with an error naming `Transformations:Assets`
