# Python transformations

Operator-supplied Python scripts that the Transformer runs on labeled Batches after Tag resolution, Quality mapping and range checks. Raw Measurements are never changed; script output is stored under derived Tags, and every run is recorded in `transformation_run`. Decisions: [ADR-0016](../../docs/adr/0016-run-python-transformations-in-a-managed-child-process.md), [ADR-0017](../../docs/adr/0017-select-transformations-by-a-label-in-batch-schema-v2.md), [ADR-0018](../../docs/adr/0018-preserve-raw-measurements-and-store-derived-tags.md).

## Examples in this folder

| Label | Script | Does | Output Tags |
| --- | --- | --- | --- |
| `pump-outliers` | `outlier_detection.py` | Flags Samples whose median/MAD z-score in the Batch exceeds 3.5 | none (dispositions only) |
| `pump-cleaning` | `data_cleaning.py` | Rejects missing/Bad Samples, flags jumps above `maxStep` and holds the previous value | `FlowRateClean`, `DischargePressureClean` |
| `celsius-to-fahrenheit` | `celsius_to_fahrenheit.py` | Converts degC Tags to `<Tag>F` | `MotorTemperatureF`, `TemperatureF` (degF) |
| `tank-normalization` | `normalization.py` | Min-max scales to 0..1 with static bounds | `LevelNorm`, `TemperatureNorm` |

They use only the standard library. Their documented results are asserted by `tests/Telemetrix.Transformer.Tests/TransformationExamplesTests.cs`.

## Enable it

1. **Python.** The Transformer needs Python 3.10 or newer. The `transformer` image installs Debian/Ubuntu `python3`; on a host run, `python3` (Linux/macOS) or `python` (Windows) must be on `PATH`, or set `Transformations__PythonExecutable`. Packages such as numpy go into that interpreter, for example a virtual environment: `python3 -m venv /opt/telemetrix-venv && /opt/telemetrix-venv/bin/pip install numpy==2.1.3`, then `Transformations__PythonExecutable=/opt/telemetrix-venv/bin/python`. Pin package versions: the script version covers the script file only.
2. **Schema.** Review and apply `deploy/sql/migrations/003_transformations.sql` once: `docker compose -f deploy/docker-compose.yml --profile migrations run --rm db-migrate`. With scripting enabled, the Transformer refuses to start without it.
3. **Folder.** The folder holds `registry.json` and the scripts. Compose mounts `deploy/transformations` read-only at `/transformations` (`TRANSFORMATIONS_DIR` in `deploy/.env` points it elsewhere). On a host run, set `Transformations__Directory` to the folder.
4. **Switch on.** `TRANSFORMATIONS_ENABLED=true` in `deploy/.env` (or `Transformations__Enabled=true`), then recreate the Transformer. It logs `Transformations ready: <label>=sha256:<hash>, …`.
5. **Assign labels.** In the Bridge: `PUMP01_TRANSFORMATION=pump-cleaning` and `TANK01_TRANSFORMATION=celsius-to-fahrenheit` in `deploy/.env` (`Transformations__Assets__<AssetId>`), then recreate the Bridge. Labeled Batches become `schemaVersion` 2; unlabeled Assets stay version 1.

```sh
docker compose -f deploy/docker-compose.yml --profile pipeline up -d --build --force-recreate bridge transformer
```

Changing a script means restarting the Transformer; it loads and hashes every script once at startup.

## Registry

```jsonc
{
  "transformations": {
    "<label>": {
      "script": "file.py",            // relative, inside this folder
      "entryPoint": "transform",      // optional, default transform
      "sha256": null,                 // optional pin; startup fails on mismatch
      "parameters": { },              // any JSON object, passed as context["parameters"]
      "outputs": {                    // Tags the script may write
        "<OutputTag>": { "unit": "degF", "minValid": -4, "maxValid": 176 }
      }
    }
  }
}
```

Labels match `^[a-z0-9][a-z0-9-]{0,62}$`. A Batch can only name a label; it can never name a file or command. Output Tags are created on first use as derived Tags (`tag.derived_by = <label>`) with the declared unit and range; afterwards the `tag` row is authoritative, as for raw Tags. An output that names a raw Tag, another label's Tag or a different unit fails the run with `output-tag-conflict`.

## Contract version 1

```python
def transform(batch: dict, context: dict) -> dict: ...
```

`batch` holds `batchId`, `schemaVersion`, `source`, `endpoint`, `plantId`, `lineId`, `assetId`, `createdAt` and `samples`, the known-Tag Samples in Batch order:

```json
{ "index": 0, "tag": "Temperature", "value": 25.0,
  "sourceTimestamp": "2026-10-31T08:00:00Z", "serverTimestamp": "2026-10-31T08:00:00.004Z",
  "statusCode": 0, "quality": "good", "unit": "degC", "minValid": -20, "maxValid": 80 }
```

`quality` is after the built-in range check; `value` may be `null`. Unknown Tags never reach the script (they go to the DLQ). `context` holds `contractVersion` (1), `label`, `version` (`sha256:…`), `parameters` and `outputs`.

The result is a dict with only these optional keys:

```json
{
  "samples": [
    { "tag": "TemperatureF", "value": 77.0, "sourceTimestamp": "2026-10-31T08:00:00Z",
      "inputs": [0], "quality": "good" }
  ],
  "dispositions": [
    { "index": 3, "disposition": "flagged", "reason": "spike" }
  ]
}
```

| Rule | Otherwise |
| --- | --- |
| `tag` is a declared output | `malformed-output` |
| `value` is a finite number (no NaN, infinity, null, string, bool) | `malformed-output` |
| `inputs` lists Sample indices; none of them rejected | `malformed-output` |
| `sourceTimestamp` equals one input's source timestamp; copy it from the input | `malformed-output` |
| `(tag, sourceTimestamp)` unique in the result | `malformed-output` |
| `quality` (optional) is `good`, `uncertain` or `bad` | `malformed-output` |
| dispositions: valid distinct indices, `flagged` or `rejected`, non-empty `reason` ≤ 1024 chars | `malformed-output` |
| at most `MaxOutputSamples` (10000) outputs | `malformed-output` |

Each input gets exactly one disposition: `rejected` / `flagged` as the script says, else `modified` when an output uses it, else `unchanged`. `{}` is a valid empty result. A derived Measurement's Quality is the worst of its inputs, Uncertain if an input is flagged, and the script's `quality`, then Bad outside the derived Tag's range: scripts can lower Quality, never raise it.

## Outcomes

| Situation | Raw Measurements | Run record | Offset |
| --- | --- | --- | --- |
| Success, including empty output | stored | `succeeded` with counts and version | committed |
| Unregistered label | stored | `failed` / `unknown-label` | committed |
| Exception (incl. `SystemExit`, `MemoryError`) | stored | `failed` / `script-error` + traceback | committed |
| Slower than `TimeoutMs` (2000) | stored | `failed` / `timeout`; worker killed and restarted | committed |
| Worker process exits | stored | `failed` / `worker-crashed`; worker restarted | committed |
| Invalid result | stored | `failed` / `malformed-output` | committed |
| Output Tag conflict | stored | `failed` / `output-tag-conflict` | committed |
| Python cannot start | — | none | held, retried with backoff |
| Scripting disabled, or Batch unlabeled | stored | none (`telemetrix.transformation.skipped`) | committed |
| Replay of a recorded Batch | unchanged (idempotent) | first record kept, script not rerun | committed |

## Writing a script

- Be deterministic and side-effect free: no network, files, clocks or randomness. A Batch can be transformed again after a crash before its commit.
- Keep state inside one Batch. There is no history across Batches; put fixed scales and thresholds in `parameters`.
- `print` is fine: it goes to the Transformer log as `Python worker: …`.
- The script runs with the Transformer's privileges. Treat the folder like configuration: only trusted operators may write to it. The child process isolates crashes, hangs and stray output, not malicious code.
- On Linux the worker's address space is limited to `MemoryLimitMb` (512).

## Check the results

```sql
SELECT label, script_version, status, reason, input_count, unchanged_count, modified_count,
       flagged_count, rejected_count, output_count, round(duration_ms::numeric, 2) AS ms, error
FROM transformation_run ORDER BY processed_at DESC LIMIT 20;

SELECT t.asset_id, t.name, t.unit, t.derived_by, m.time, m.value, m.quality
FROM measurement m JOIN tag t USING (tag_id)
WHERE t.derived_by IS NOT NULL ORDER BY m.time DESC LIMIT 20;

SELECT d.label, t.name, d.time, d.disposition, d.reason
FROM sample_disposition d JOIN tag t USING (tag_id) ORDER BY d.time DESC LIMIT 20;
```

Metrics on `:5102/metrics`: `telemetrix_transformation_runs_total`, `telemetrix_transformation_duration_milliseconds`, `telemetrix_transformation_samples_in_total`, `telemetrix_transformation_samples_out_total`, `telemetrix_transformation_skipped_total`.
