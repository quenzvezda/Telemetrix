# Design

## Context

The Transformer processes one Batch at a time per consumer thread: `RawBatchProcessor` validates the payload, `TelemetryBatchHandler` resolves Tags (unknown Tags go to the DLQ), maps Quality, applies range checks and writes through `MeasurementWriter` (binary COPY into a staging table, `INSERT … ON CONFLICT DO NOTHING`), then the offset is committed. Storage failures retry forever with backoff. Everything else in this design hangs off that single seam.

## Goals / Non-Goals

**Goals:**
- Label-selected, operator-supplied Python scripts, pinned by content hash, with a documented versioned contract.
- No change to raw Measurements, delivery guarantees, ordering or the default (unlabeled / disabled) behaviour.
- Every outcome observable and durable; nothing lost silently.

**Non-Goals:**
- Cross-Batch (historical) state, windows across Batches or external lookups.
- Sandboxing hostile code; scripts are trusted operator configuration (ADR-0016).
- Re-transforming stored history after a script change (controlled replay, issue #11) and the clean topic (ADR-0012).
- Hot reload of scripts; a script change is a Transformer restart.

## Decisions

### Runtime (ADR-0016)
One long-lived `python -I -u telemetrix_runner.py` worker per Transformer process, newline-delimited JSON over stdin/stdout. The runner duplicates the protocol stdout and redirects fd 1 to stderr, so `print` in a script cannot corrupt the protocol; stderr lines are logged. Messages:

| Request | Response |
| --- | --- |
| (start) | `{"ok":true,"ready":true,"contractVersion":1,"python":"3.12.4"}` |
| `{"op":"load","label","version","filename","entryPoint","source"}` | `{"ok":true}` or `{"ok":false,"reason":"load-failed","message"}` |
| `{"op":"transform","label","batch","context"}` | `{"ok":true,"result":{…}}` or `{"ok":false,"reason":"script-error"\|"malformed-output","message"}` |

`Transformations` options: `Enabled` (false), `Directory` (`transformations`), `PythonExecutable` (`python3`, `python` on Windows), `TimeoutMs` (2000), `MaxOutputSamples` (10000), `MemoryLimitMb` (512, POSIX `RLIMIT_AS`, 0 disables). A timeout or worker exit kills the process tree and records `timeout` / `worker-crashed`; the next Batch restarts the worker and reloads all scripts. Failing to start the worker is transient and retried with the storage backoff, holding the offset.

### Registry
`<Directory>/registry.json`:
```json
{
  "transformations": {
    "celsius-to-fahrenheit": {
      "script": "celsius_to_fahrenheit.py",
      "entryPoint": "transform",
      "sha256": null,
      "parameters": { "sourceUnit": "degC" },
      "outputs": {
        "MotorTemperatureF": { "unit": "degF", "minValid": -4, "maxValid": 248 }
      }
    }
  }
}
```
Labels match `^[a-z0-9][a-z0-9-]{0,62}$`. `script` is a relative path that must stay inside the directory. `entryPoint` defaults to `transform`. The version is `sha256:<hex>` of the script bytes; a non-null `sha256` pins it and startup fails on mismatch. Output names follow the Tag name rules and `unit` is required. Startup fails on any invalid entry, missing or unloadable script, or missing migration 003.

### Input/output contract, version 1
The entry point is called as `transform(batch, context) -> dict`.

`batch`: `batchId`, `schemaVersion`, `source`, `endpoint`, `plantId`, `lineId`, `assetId`, `createdAt` and `samples`, each with `index`, `tag`, `value` (number or null), `sourceTimestamp`, `serverTimestamp` (UTC ISO-8601), `statusCode` (raw), `quality` (`good` / `uncertain` / `bad` after the built-in range check), `unit`, `minValid`, `maxValid`. Only known-Tag Samples are included, in Batch order.

`context`: `contractVersion` (1), `label`, `version`, `parameters` (registry JSON), `outputs` (declared output Tags with `unit`, `minValid`, `maxValid`).

Result: an object with only these optional keys:
- `samples`: `[{ "tag", "value", "sourceTimestamp", "inputs": [index…], "quality"? }]`
- `dispositions`: `[{ "index", "disposition": "flagged" | "rejected", "reason" }]`

Validation (any violation → `malformed-output`, nothing from the script is stored): result is an object without unknown keys; at most `MaxOutputSamples` outputs; `tag` is a declared output; `value` is a finite number (NaN, infinity, null, strings and booleans are invalid); `inputs` is non-empty, in range, not rejected; `sourceTimestamp` equals the source timestamp of one listed input; `(tag, sourceTimestamp)` is unique within the result; `quality` if present is one of the three names; dispositions reference valid, distinct indices and carry a non-empty `reason` ≤ 1024 characters. `{}` and empty `samples` / `dispositions` lists are a valid empty result: all inputs `unchanged`, zero outputs.

### Position in the pipeline (ADR-0018)
```
validate payload → resolve Tags (unknown → DLQ) → Quality + range check (raw)
   → [label? enabled? not yet recorded?] run script → validate → resolve derived Tags → derived Quality + range check
   → one transaction: run row, raw + derived Measurements, dispositions → commit offset
```
The script never sees unknown Tags and never changes raw Measurements. Derived Quality = max(input Qualities, Uncertain if any input flagged, script quality), then Bad when outside the derived Tag's range.

### Selector and compatibility (ADR-0017)
`TelemetryBatch.Transformation` (nullable, omitted when null). Bridge `Transformations:Assets:<AssetId>` = label (empty means none) → `schemaVersion` 2; otherwise version 1. The Transformer accepts version 1 without the field and version 2 with an optional valid label.

| Batch | Scripting disabled | Scripting enabled |
| --- | --- | --- |
| unlabeled (v1 / v2 without label) | built-in only | built-in only |
| registered label | built-in only; `skipped{reason=disabled}` and a warning | built-in + script; run row |
| unregistered label | built-in only; skipped as above | built-in only; run row `failed/unknown-label` |
| label with existing run row | n/a | built-in only (idempotent); `skipped{reason=already-recorded}` |

### Storage
Migration `003_transformations.sql`:
```sql
ALTER TABLE tag ADD COLUMN IF NOT EXISTS derived_by text;
CREATE TABLE IF NOT EXISTS transformation_run (
  batch_id uuid, label text, asset_id text NOT NULL, script_version text,
  status text NOT NULL CHECK (status IN ('succeeded','failed')), reason text, error text,
  input_count int, unchanged_count int, modified_count int, flagged_count int,
  rejected_count int, output_count int, duration_ms double precision,
  processed_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (batch_id, label));
CREATE TABLE IF NOT EXISTS sample_disposition (
  batch_id uuid, label text, sample_index int, tag_id int REFERENCES tag, time timestamptz,
  disposition text CHECK (disposition IN ('flagged','rejected')), reason text NOT NULL,
  PRIMARY KEY (batch_id, label, sample_index));
```
Derived Tags are created on first output with `INSERT … ON CONFLICT DO NOTHING` and checked: the row must have `derived_by` = label and the declared unit; otherwise `output-tag-conflict`. After creation the `tag` row's range is authoritative, as for raw Tags.

`MeasurementWriter` inserts the run row first (`ON CONFLICT DO NOTHING`); only if it was inserted are derived Measurements and dispositions staged, so one Batch never mixes script versions. The handler checks for an existing run row before invoking the script to avoid re-running it on replay.

### Observability
Meter `Telemetrix.Transformer`: `telemetrix.transformation.runs` {run} and `telemetrix.transformation.duration` (ms) with `label`, `version` (first 12 hex digits) and `outcome` (`succeeded` or the failure reason); `telemetrix.transformation.samples.in` {sample} with `label`, `disposition`; `telemetrix.transformation.samples.out` {sample} with `label`; `telemetrix.transformation.skipped` {batch} with `reason`. Labels not in the registry are reported as `unregistered`. Logs carry the `batchId` scope, label, version, outcome, counts and duration.

### Failure classes
| Outcome | Class | Offset | Durable record |
| --- | --- | --- | --- |
| unknown-label, script-error, timeout, worker-crashed, malformed-output, output-tag-conflict | permanent | committed after the transaction | `transformation_run` failed + reason + error |
| worker cannot start, database error | transient | held, retried with backoff | none until success |
| missing / invalid script, bad registry, missing migration | configuration | not consumed | startup fails |

## Risks / Trade-offs

- [A slow script delays every Asset on the partition] → per-Batch timeout (default 2 s) bounds the delay; measured cost is documented against NFR-06/NFR-07.
- [A script with side effects runs twice after a crash] → documented prohibition; the run row prevents re-running only after a durable commit.
- [Python packages change behaviour without a script hash change] → pin packages in the image; the version names the script only.
- [Derived Tags multiply storage] → they are ordinary Tags under existing retention and rollups.
- [A buggy script fails every Batch] → failures are counted per label and recorded; raw data continues to flow.

## Migration Plan

Scripting is disabled by default and needs no migration. To enable: review and apply `003_transformations.sql` with the explicit `migrations` profile, mount the scripts directory, set `Transformations__Enabled=true` and assign labels in the Bridge. Rollback: disable scripting or remove the labels; derived data stays queryable.
