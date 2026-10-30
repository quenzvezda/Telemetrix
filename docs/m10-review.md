# M10 review package: Python transformations

Change: [`add-python-transformations`](../openspec/changes/archive/2026-10-31-add-python-transformations/) · Issue: [#7](https://github.com/quenzvezda/Telemetrix/issues/7) · Branch: `m10-add-python-transformations`

**Accepted:** the reviewer approved the PR on 2026-10-31; the change is archived and its requirements are in `openspec/specs/`.

On 2026-10-31 the reviewer asked for issue #7 to be implemented to completion, using the agent's recommendation for each open decision. This package lists those decisions first, because they are the part most worth challenging, then the code to read, the automated evidence and the manual checks for the issue's acceptance criteria.

## 1. Decisions to review

| Issue question | Recommendation taken | ADR | Main alternative rejected |
| --- | --- | --- | --- |
| Runtime boundary | One long-lived `python -I -u` child process, NDJSON over stdin/stdout, per-Batch timeout (2 s), kill + restart on timeout or exit, address-space limit on Linux (512 MB). Scripts are trusted operator configuration. | [0016](adr/0016-run-python-transformations-in-a-managed-child-process.md) | Embedded interpreter (a script fault kills the consumer); process per Batch (~30–60 ms start-up each) |
| Selector location and schema evolution | Optional `transformation` label in Batch `schemaVersion` 2, assigned per Asset by the Bridge. Unlabeled Batches stay v1 byte-for-byte. | [0017](adr/0017-select-transformations-by-a-label-in-batch-schema-v2.md) | Kafka header (lost in edge buffer and DLQ payloads) |
| Label resolution, version pinning | Registry file in the scripts folder; SHA-256 of each script at startup, optional pin; versions fixed until restart. | 0016, 0017 | Hot reload (non-deterministic replays) |
| Position in the pipeline | After Tag resolution, Quality mapping and raw range check; outputs range-checked against the derived Tag. | [0018](adr/0018-preserve-raw-measurements-and-store-derived-tags.md) | Before range checks (converted values checked against the wrong unit) |
| Raw vs transformed storage | Raw Measurements never change. Output goes to derived Tags (`tag.derived_by`), plus `transformation_run` and `sample_disposition`, all in the Batch's transaction. | 0018 | Overwriting raw values (breaks NFR-01 and replay identity) |
| Stateless vs windowed | Stateless per Batch; windows only inside one Batch; scales and thresholds from static parameters. | 0018 | Cross-Batch state (ordering, bounds and recovery would all need design) |
| Permanent vs transient failures | Unknown label, exception, timeout, crash, malformed output, Tag conflict → durable failed run, raw stored, offset committed. Python cannot start → offset held, retry with backoff. | 0016, 0018 | Dead-lettering transformation failures (replay belongs to issue #11) |
| Replay determinism | First durable run per `(batch_id, label)` is final; a replay never reruns the script, even after a script change. | 0018 | Rerun on replay (mixes versions) |

Unlabeled Batches and disabled scripting keep the exact pre-M10 behaviour. Disabled scripting needs no migration.

## 2. Commits and what to read

| Commit | Read |
| --- | --- |
| `docs(adr): record python transformation decisions for m10` | ADR-0016–0018, `openspec/changes/archive/2026-10-31-add-python-transformations/design.md` (contract and outcome tables) |
| `feat(contracts): add batch schema v2 with a transformation label` | `TelemetryBatch.cs`, `TransformationLabel.cs`, `TelemetryJson.DeserializeValidated` |
| `feat(bridge): assign transformation labels per asset` | `TransformationAssignmentOptions.cs`, `AssetBatcher` flush |
| `feat(transformer): load the python transformation registry` | `Scripting/TransformationRegistry.cs` (path containment, hash pin) |
| `feat(transformer): run transformations in a managed python worker` | `Scripting/telemetrix_runner.py` (fd 1 redirection), `Scripting/PythonWorker.cs` (timeout kill, restart, half-loaded guard) |
| `feat(transformer): validate transformation output into dispositions` | `TransformationOutputValidator.cs`, `TransformationEngine.cs` (Quality only goes down) |
| `feat(transformer): store transformation runs with the batch atomically` | `003_transformations.sql`, `MeasurementWriter.cs` (run row first, derived rows only if it is new), `PostgresDerivedTagCatalog.cs`, `PostgresTagCatalog` raw-only filter, `TransformationStage.cs`, DI |
| `test(transformer): cover transformation metrics and run logs` | `TransformerMetrics.TransformationCompleted` |
| `docs(deploy): add four runnable python transformation examples` | `deploy/transformations/` scripts and README |
| `build(deploy): ship python3 and mount transformation scripts` | Dockerfile, compose, `.env.example`, `.gitattributes` |
| `test(transformer): verify …` / `prove replay …` | `TransformationPipelineTests`, `TransformationReplayTests` |

## 3. Automated evidence

| Check | Result |
| --- | --- |
| `dotnet build -c Release` | 0 warnings, 0 errors |
| `dotnet test -c Release` | 333 tests: 332 passed, 1 skipped (the explicit long `LoadProfileTests` profile), 0 failed; integration tests 6 min 22 s |
| `openspec validate add-python-transformations --strict` | valid |
| PR #14 CI | `secrets`, `build-test`, `openspec`, `delivery-review` (M4 SIGKILL) and `edge-review` (M5 outage) all passed |
| Two Assets, two scripts (`TransformationPipelineTests`) | Pump01 → `pump-cleaning`, Tank01 → `celsius-to-fahrenheit`, two distinct `sha256:` versions recorded |
| Outcomes (`TransformationPipelineTests`) | unknown-label, script-error (with traceback), timeout, malformed-output (NaN), output-tag-conflict, empty success; raw Measurements of all ten Batches stored unchanged; worker restarted after the timeout |
| Compatibility | v1 Batch with scripting on: no run row; labeled Batch with scripting off and no migration: stored and committed; scripting on without migration: startup refused |
| Replay / crash (`TransformationReplayTests`) | killed after the DB commit, restarted with a changed script and a duplicate delivery: 6 rows, run keeps the original version and values. Killed before the commit: the new version is the only result |
| Examples (`TransformationExamplesTests`) | all four through the real runner with the README's documented results |
| Runtime cost (`TransformationPerformanceTests`, 500 Samples, 200 runs, this workstation) | `celsius-to-fahrenheit` p50 4.655 ms, p95 6.797 ms · `pump-cleaning` p50 3.553 ms, p95 5.589 ms · `pump-outliers` p50 2.040 ms, p95 3.769 ms |
| Linux image | `transformer` target: Ubuntu 24.04.5, Python 3.12.3, runner handshake with the 512 MB limit applied; image 554 MB |

**NFR-06 / NFR-07.** At 5,000 samples/s and 500 Samples per Batch the Transformer handles 10 Batches/s. The worst measured p95 (6.8 ms) adds under 70 ms of work per second (< 7 % of the single consumer thread) and under 10 ms to a Batch's latency, against the M8 baseline p95 of 7,774.686 ms and the 10 s budget. Derived rows ride in the same COPY and transaction. A full 15-minute load run with scripting enabled was **not** repeated; the M8 profiles stay the recorded NFR-06/NFR-07 evidence with scripting off (default). The timeout bounds a slow script at 2 s per Batch, which would exceed NFR-06 if every Batch hit it; such runs are visible as `timeout` outcomes.

## 4. Manual checks

All commands run from the repository root in PowerShell, on the existing M9 volumes (do **not** use `down -v`; PlantSim and Bridge certificates stay trusted).

### 4.1 Apply the migration and enable scripting

Review `deploy/sql/migrations/003_transformations.sql`, then:

```powershell
docker compose -f deploy/docker-compose.yml --profile migrations run --rm db-migrate
```

Expected: `Applying 001_…`, `Applying 002_…`, `Applying 003_transformations.sql`, exit code 0 (all three are idempotent).

Add to `deploy/.env`:

```
TRANSFORMATIONS_ENABLED=true
PUMP01_TRANSFORMATION=pump-cleaning
TANK01_TRANSFORMATION=celsius-to-fahrenheit
```

```powershell
docker compose -f deploy/docker-compose.yml --profile pipeline up -d --build
docker compose -f deploy/docker-compose.yml logs transformer | Select-String "Transformations ready|Loaded transformation|Started Python"
```

Expected: `Started Python worker … with Python 3.12.3`, four `Loaded transformation <label> version sha256:…` lines and one `Transformations ready: …` line.

### 4.2 Two Batches, two scripts (acceptance 1)

Kafka UI <http://localhost:8080> → Topics → `plant.telemetry.raw` → Messages. Expected: Pump01 messages contain `"schemaVersion":2` and `"transformation":"pump-cleaning"`; Tank01 messages `"transformation":"celsius-to-fahrenheit"`.

```powershell
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -U telemetry -d telemetry -c "SELECT label, script_version, status, count(*), sum(input_count) AS inputs, sum(output_count) AS outputs FROM transformation_run GROUP BY 1,2,3 ORDER BY 1"
```

Expected: one `succeeded` row group per label, each with its own `sha256:` version; `celsius-to-fahrenheit` outputs ≈ Tank01 `Temperature` Samples, `pump-cleaning` outputs ≈ FlowRate + DischargePressure Samples.

### 4.3 Units, Quality and lineage (acceptance 6)

```powershell
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -U telemetry -d telemetry -c "SELECT asset_id, name, unit, min_valid, max_valid, derived_by FROM tag WHERE derived_by IS NOT NULL ORDER BY 1,2"
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -U telemetry -d telemetry -c "SELECT count(*) AS pairs, count(*) FILTER (WHERE abs(f.value - (c.value*9/5+32)) > 1e-9) AS wrong, count(*) FILTER (WHERE f.batch_id <> c.batch_id) AS other_batch FROM measurement c JOIN tag tc ON tc.tag_id=c.tag_id AND tc.name='Temperature' JOIN tag tf ON tf.asset_id=tc.asset_id AND tf.name='TemperatureF' JOIN measurement f ON f.tag_id=tf.tag_id AND f.time=c.time"
```

Expected: `DischargePressureClean` (bar, 0–8), `FlowRateClean` (m3/h, 0–80) for Pump01 and `TemperatureF` (degF, −4–176) for Tank01, each with its label in `derived_by`. Second query: `pairs` > 0, `wrong` = 0, `other_batch` = 0 (same source timestamp and same Batch as the raw input).

Quality propagation: in UaExpert call PlantSim `SetBadQuality("Temperature", 30)`, wait 40 s, then:

```powershell
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -U telemetry -d telemetry -c "SELECT count(*) FILTER (WHERE c.quality=2) AS bad_raw, count(*) FILTER (WHERE c.quality=2 AND f.quality=2) AS bad_both FROM measurement c JOIN tag tc ON tc.tag_id=c.tag_id AND tc.name='Temperature' JOIN tag tf ON tf.asset_id=tc.asset_id AND tf.name='TemperatureF' JOIN measurement f ON f.tag_id=tf.tag_id AND f.time=c.time WHERE c.time > now() - interval '2 minutes'"
```

Expected: `bad_raw` > 0 and `bad_both` = `bad_raw`: every Bad Celsius input with a value yields a Bad Fahrenheit output (a script can never raise Quality). Inputs without a value produce no output.

Dispositions: call `Spike("FlowRate")`, wait 10 s:

```powershell
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -U telemetry -d telemetry -c "SELECT d.label, t.name, d.time, d.disposition, d.reason FROM sample_disposition d JOIN tag t USING (tag_id) ORDER BY d.time DESC LIMIT 5"
```

Expected: a `pump-cleaning` / `FlowRate` row `rejected`, reason `bad quality (status or range)`; the raw FlowRate spike is still in `measurement` with quality 2 and there is no `FlowRateClean` row at that time.

### 4.4 Failures are observable, nothing is lost (acceptance 4)

Set `TANK01_TRANSFORMATION=made-up` in `deploy/.env`, then:

```powershell
docker compose -f deploy/docker-compose.yml --profile pipeline up -d bridge
Start-Sleep 20
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -U telemetry -d telemetry -c "SELECT label, status, reason, script_version, input_count, processed_at FROM transformation_run ORDER BY processed_at DESC LIMIT 3"
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -U telemetry -d telemetry -c "SELECT max(m.time) FROM measurement m JOIN tag t USING (tag_id) WHERE t.name='Level'"
```

Expected: `made-up | failed | unknown-label | (null)` rows, and the latest Tank01 `Level` time keeps advancing. Restore `TANK01_TRANSFORMATION=celsius-to-fahrenheit` and run the same `up -d bridge`.

Exceptions, timeouts, crashes, malformed and empty output are covered by `TransformationPipelineTests`; to see one live, add a script that raises to `deploy/transformations` and register it — the run shows `script-error` with the traceback in `error`.

### 4.5 Disabled scripting and unlabeled Batches (acceptance 2)

Set `TRANSFORMATIONS_ENABLED=false`, then `docker compose -f deploy/docker-compose.yml --profile pipeline up -d transformer`. Expected: one warning per label `Scripting is disabled; batches labeled … use the built-in pipeline only`, no new `transformation_run` rows, raw rows keep arriving, and:

```powershell
(Invoke-WebRequest http://localhost:5102/metrics).Content -split "`n" | Select-String "telemetrix_transformation"
```

shows the `skipped` counter with `reason="disabled"`. Remove the two `*_TRANSFORMATION` lines and recreate the Bridge: messages in Kafka UI go back to `"schemaVersion":1` with no `transformation` field.

### 4.6 Metrics and dashboard (acceptance 7)

With scripting enabled again, the metrics output above lists runs, duration, samples in (by disposition) and out per `label`, `version` and `outcome`. In Grafana <http://localhost:3000>, choose Asset `Tank01`: the Tag selector now offers `TemperatureF` next to `Temperature`, and both trends move together on their own scales.

## 5. Known limitations

- Scripts are trusted code; the child process is not a sandbox.
- Python packages are outside the script hash; pin them in the image.
- Re-transforming history after a script change needs controlled replay (issue #11).
- Lineage is per Batch (run row + `batch_id`), not per output-to-input pair.
- The M8 load profiles were not re-run with scripting enabled; see section 3.
