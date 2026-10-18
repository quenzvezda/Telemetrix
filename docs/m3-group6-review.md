# M3 group 6 review: final milestone checks

Change: `add-end-to-end-slice`, tasks 6.0–6.1.
Branch: `m3-add-end-to-end-slice`.
Status: both tasks complete; awaiting final group review before archive/merge.

## Task 6.0: fail when a test project runs no tests

Removed the temporary `--ignore-exit-code 8` setting and its comment from
`tests/Directory.Build.props`. Every test project now has real cases, so an empty
test run must no longer be treated as success. Existing xUnit and shared MSBuild
settings remain in place. No additional test case is needed for this configuration
change; verification runs the actual complete suite with the normal exit policy.

On 2026-10-18, `dotnet build -c Release` passed with zero warnings/errors.
`dotnet test -c Release` exited 0 with all 103 cases passed, no failures/skips,
in 2m 30.087s: 55 Bridge unit cases, 34 Transformer unit cases and 14 integration
cases. Database/Kafka/OPC UA integration checks used disposable test containers.

## Task 6.1: reviewer live-trend evidence

The reviewer supplied the following local Compose evidence on 2026-10-18 and
authorized group 6 after its assessment. This records the actual manual run;
the agent did not rerun the services or mutate the local database.

- All six infrastructure services were up; Kafka and TimescaleDB were healthy.
- Bridge subscribed to the seven configured OPC UA tags and published Pump01 and
  Tank01 batches on partitions 2 and 3, including final batches on shutdown.
- Transformer loaded the catalog before consuming, received all six raw
  partitions in group `transformer`, and logged successful writes/handling with
  the original batch ids. Both services returned to their terminal prompts.
- The grouped SQL query returned all seven seeded tags with nonzero counts:

  | Asset | Tag | Measurements at the captured query |
  | --- | --- | ---: |
  | Pump01 | DischargePressure | 5720 |
  | Pump01 | FlowRate | 5720 |
  | Pump01 | MotorTemperature | 387 |
  | Pump01 | Running | 387 |
  | Pump01 | Vibration | 5720 |
  | Tank01 | Level | 386 |
  | Tank01 | Temperature | 1961 |

- Stored rows carried source time, value, quality, batch id and ingest time.
  The shown out-of-range pressure values had quality 2, while the shown FlowRate
  values had quality 0. Batch `01a14f49-4e74-7c04-b82e-c6dacef4b4f6` matched the
  Bridge's Pump01 publish log at partition 2, offset 359.
- The milestone SQL query returned MotorTemperature increasing from 402.1 at
  `2026-10-18 13:51:27.912366+00` to 403.8 at
  `2026-10-18 13:52:50.112579+00`, including regular five-second source-time
  updates. This satisfies the M3 live-row/trend criterion. The OPC PLC signal is
  a simulator trend; its values need not stay within the engineering range.
- The reviewer also ran all 103 tests successfully (0 failures/skips) and strict
  OpenSpec validation (9 passed).

### Backlog observation

The Kafka UI screenshot was taken with group `transformer` in `EMPTY` state and
zero members. Its total lag was 16 batch messages:

| Partition | Last handled offset in Transformer log | Current offset in UI | End offset in UI | Lag |
| --- | ---: | ---: | ---: | ---: |
| 2 | 364 | 365 | 373 | 8 |
| 3 | 362 | 363 | 371 | 8 |

Bridge logs continued through offsets 372 and 370 respectively, consistent with
publishing after Transformer stopped. The supplied evidence does not demonstrate
zero lag or loss-free crash recovery. M3 keeps default auto-commit/store; delivery
guarantees remain M4 work. No offsets were reset and no messages were deleted.
Backlog draining has not been verified and is not recorded as passed.

## Commits and diff review

One commit records each task:

- `7d411e8` — test exit-policy change (6.0).
- `docs(openspec): record live trend acceptance for m3` — live verification
  evidence and milestone review status (6.1).

```powershell
git log --oneline --reverse cabb352..HEAD
git diff cabb352..HEAD -- tests/Directory.Build.props
git diff cabb352..HEAD -- ROADMAP.md README.md openspec/changes/add-end-to-end-slice/tasks.md docs/m3-group5-review.md docs/m3-group6-review.md
```

Check the two deleted MSBuild lines, the provenance of the manual results above,
and the distinction between completed implementation and an archived milestone.

## Reviewer checks

Stop any running Bridge/Transformer instances before building. From the repo root,
with Docker running:

```powershell
dotnet build -c Release
dotnet test -c Release
$LASTEXITCODE
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
npx -y @fission-ai/openspec@1.14.3 list
```

Expected: zero build warnings/errors, 103 tests passed with no skips, test exit
code `0`, nine OpenSpec items valid, and `add-end-to-end-slice` at 14/14 tasks.
The build is necessary so test runners receive the changed MSBuild configuration.
The live SQL verification already supplied above does not need to be repeated
for this two-line configuration change.

If checking the outstanding backlog, leave Bridge stopped and restart only
Transformer with the connection setup in [the G5 review](m3-group5-review.md).
In [Kafka UI](http://localhost:8080/ui/clusters/local/consumer-groups), open group
`transformer`, enable refresh and wait for lag 0 while it is running. Then stop
Transformer. This is an optional backlog check; no offset reset is needed.

To repeat the milestone's read-only SQL check:

```powershell
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c "SELECT time, value FROM measurement JOIN tag USING (tag_id) WHERE name = 'MotorTemperature' ORDER BY time DESC LIMIT 20"
```

Expected: the stored trend shown above, or newer measurements if the services
have run again. Grafana dashboards are M8 work; this group changes no Grafana setup.

## Final review checkpoint

Strict OpenSpec validation passed all nine items on 2026-10-18. The refreshed
apply instructions report `all_done`, with 14/14 tasks complete. ROADMAP.md marks
M3 In review; implementation and manual live-trend verification are recorded,
while the change remains active and the branch has not been merged.

Review group 6 before the archive/merge handoff, following AGENTS.md's task-group
checkpoint. Archive, spec synchronization and merge into `main` are not part of
this apply run. M4 has not started; D4 remains its open decision.
