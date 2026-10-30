# Proposal

## Why

Operators want to detect outliers, clean values, convert units or normalise Samples with their own Python scripts, selected per Batch, without rebuilding the C# Transformer ([issue #7](https://github.com/quenzvezda/Telemetrix/issues/7)). Today `TelemetryBatchHandler` resolves Tags, maps Quality, checks ranges and writes Measurements; there is no extension point, and Batch schema version 1 has no transformation selector.

Milestone: M10 — Python transformations (post-M9 backlog). On 2026-10-31 the reviewer asked for issue #7 to be implemented end to end, using the agent's recommendation for each open design decision; the decisions are recorded in ADR-0016, ADR-0017 and ADR-0018 for review.

## What Changes

- FR-26: the Bridge assigns an optional transformation label per Asset; labeled Batches use `schemaVersion` 2 with a `transformation` field, unlabeled Batches stay version 1 (ADR-0017).
- FR-21: the Transformer accepts Batch schema versions 1 and 2 and dead-letters an invalid label as a schema violation.
- FR-27: the Transformer loads a registry of labels → Python scripts from a local directory, pins each script by SHA-256 at startup and runs the selected script in a managed child process after Tag resolution, Quality mapping and range checks (ADR-0016).
- FR-27: script output is validated before storage; unchanged, modified, flagged, rejected and derived Samples, empty output and every failure (unknown label, exception, timeout, crash, malformed output, Tag conflict) have explicit, durable outcomes.
- FR-28: raw Measurements are never changed; output is stored as derived Measurements under derived Tags with their own unit and range; a `transformation_run` row and flagged/rejected dispositions are written in the same transaction as the Measurements before the offset is committed (FR-24, NFR-01, NFR-03, ADR-0018).
- NFR-05: per-Asset ordering and source timestamps are unchanged; outputs carry an input's source timestamp.
- FR-41, NFR-12: transformation duration, outcomes and input/output counts are exposed as metrics and correlated logs with the Batch id and script version.
- NFR-06, NFR-07: the per-Batch cost of the runtime is measured and documented.
- Runnable examples for outlier detection, data cleaning, Celsius-to-Fahrenheit conversion and normalisation, with installation and folder-mount instructions.

## Capabilities

### New Capabilities
- `python-transformations`: registry, runtime, input/output contract, validation, dispositions, derived storage and observability of label-selected Python transformations.

### Modified Capabilities
- `telemetry-batching`: per-Asset transformation label assignment.
- `telemetry-transform`: schema version 2 acceptance and commit after the transformation record.

## Impact

- Contracts: `TelemetryBatch.Transformation`, schema version 2, shared label rule.
- Bridge: `Transformations:Assets` configuration and `AssetBatcher`.
- Transformer: new `Scripting` module (registry, Python worker, validator, engine), `TelemetryBatchHandler`, `MeasurementWriter`, metrics; Python runner shipped next to the binary.
- Database: migration `deploy/sql/migrations/003_transformations.sql`, applied explicitly; needed only when scripting is enabled.
- Deployment: `python3` in the Transformer image, `deploy/transformations/` examples mounted read-only, compose variables; scripting stays disabled by default.
- Docs: ADR-0016–0018, CONTEXT.md terms, design document requirements, README and review package.
