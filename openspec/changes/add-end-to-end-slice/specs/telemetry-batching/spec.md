## ADDED Requirements

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
