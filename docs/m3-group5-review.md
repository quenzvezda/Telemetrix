# M3 group 5 review: Transformer storage

Change: `add-end-to-end-slice`, tasks 5.1–5.2.
Branch: `m3-add-end-to-end-slice`.
Status: awaiting reviewer code review and manual verification before group 6.

## Task 5.1

`MeasurementWriter` copies validated samples into a temporary staging table and inserts
them into `measurement` in one transaction. The existing `(tag_id, time)` constraint
keeps the first measurement on replay. Input order also determines the first value
when a batch repeats that identity. Source timestamps become UTC `timestamptz`,
values remain nullable, and the database supplies `ingested_at`.

Verified on 2026-10-18 with disposable TimescaleDB 2.30.2 / PostgreSQL 17 containers:

```powershell
dotnet test --project tests/Telemetrix.Integration.Tests/Telemetrix.Integration.Tests.csproj -c Release -- --filter-class Telemetrix.Integration.Tests.MeasurementWriterTests
```

All three cases passed: 35 rows with exact values, quality, source timestamps and
batch id; replay with either batch id inserts zero; an exception during the insert
leaves zero rows and the same pooled connection can successfully retry. Coverage
also includes null values, UTC conversion and duplicate identities within a batch.

## Task 5.2

The executable now starts the full Transformer pipeline. Startup validates Kafka,
policy and PostgreSQL connection settings, initializes the owned DLQ and loads the
tag cache before raw consumption starts. `TelemetryBatchHandler` resolves tags,
maps status severity and checks configured ranges, awaits the bulk write, then
publishes any unknown-tag subset. Database and DLQ failures propagate to the
consumer and stop the host. Logs report batch id, known/inserted/unknown counts,
and the consumer's partition/offset.

Storage commits precede DLQ publishing. These are separate systems: DLQ failure
can leave known measurements stored already. Reprocessing remains safe for the
measurement identity, but DLQ publication is not deduplicated. M3 still uses
Kafka's default auto-commit/store; a crash or cancellation can commit ahead of
storage. Commit-after-write, retry and malformed-message handling belong to M4.

The real host integration test produces shared JSON into a six-partition Kafka
topic and checks TimescaleDB rows under both unknown-tag policies. It verifies
Good/Uncertain/Bad, range violation, null values, source time rather than server
or batch time, original batch ids, default DLQ subset/header, and opt-in metadata
creation. Unit tests check pipeline composition and propagation of writer/DLQ
failures. No schema change or migration is required.

## Commits and diff review

Read task 5.1 commit `02bf305` and the subsequent
`feat(transformer): wire raw consumption to measurement storage` commit (5.2):

```powershell
git log --oneline --reverse 0f76fd9..HEAD
git diff 0f76fd9..HEAD -- src/Telemetrix.Transformer tests/Telemetrix.Transformer.Tests tests/Telemetrix.Integration.Tests
git diff 0f76fd9..HEAD -- README.md docs/m3-group5-review.md openspec/changes/add-end-to-end-slice/tasks.md
```

Check transaction scope and binary COPY types, UTC source time, nullable values,
the conflict key, startup ordering, default policy, and failure propagation.
Existing measurements win on replay even if a later batch has a different id.
`Stored batch` logs distinguish staged, inserted and duplicate counts.

## Manual verification

Run from the repository root. Stop existing Bridge/Transformer instances with
Ctrl+C before building to avoid locked Windows output files.

1. Build and test the storage group with Docker running:

   ```powershell
   dotnet build -c Release
   dotnet test --project tests/Telemetrix.Transformer.Tests/Telemetrix.Transformer.Tests.csproj --no-build -c Release
   dotnet test --project tests/Telemetrix.Integration.Tests/Telemetrix.Integration.Tests.csproj --no-build -c Release -- --filter-class Telemetrix.Integration.Tests.MeasurementWriterTests --filter-class Telemetrix.Integration.Tests.TransformerPipelineTests
   ```

   Expected: clean build, 34 Transformer unit cases and five targeted integration
   cases passed. The integration tests use disposable containers, not Compose data.

2. Start the existing local stack and one Bridge instance:

   ```powershell
   docker compose -f deploy/docker-compose.yml up -d
   docker compose -f deploy/docker-compose.yml ps -a
   dotnet run --project src/Telemetrix.Edge.Bridge --no-build -c Release
   ```

   Expected: infrastructure is up, then raw-topic initialization and published
   Pump01/Tank01 batches. Keep this terminal running. Existing volumes are kept.

3. In a second dedicated terminal, configure the password and run Transformer:

   ```powershell
   $dbPassword = Read-Host 'POSTGRES_PASSWORD from deploy/.env' -AsSecureString
   $quotedPassword = ([System.Net.NetworkCredential]::new('', $dbPassword).Password).Replace('"', '""')
   $env:ConnectionStrings__Telemetry = 'Host=localhost;Port=5432;Database=telemetry;Username=telemetry;Password="' + $quotedPassword + '"'
   Remove-Variable dbPassword, quotedPassword
   try {
       dotnet run --project src/Telemetrix.Transformer --no-build -c Release
   } finally {
       Remove-Item Env:ConnectionStrings__Telemetry
   }
   ```

   Expected: DLQ creation/existence, `Loaded tag catalog`, raw partition assignment
   in group `transformer`, and `Stored batch` / `Processed batch` logs with nonzero
   inserts. The first run can consume retained raw batches before catching up.
   Missing/invalid `ConnectionStrings:Telemetry` must fail startup; set the actual
   local password only in this terminal. Do not paste it into review output.

4. In a third terminal, inspect stored measurements using read-only SQL:

   ```powershell
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c "SELECT t.asset_id, t.name, count(*) AS samples, max(m.time) AS latest_source_time FROM measurement m JOIN tag t USING (tag_id) GROUP BY t.asset_id, t.name ORDER BY t.asset_id, t.name"
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c "SELECT t.asset_id, t.name, m.time, m.value, m.quality, m.batch_id, m.ingested_at FROM measurement m JOIN tag t USING (tag_id) ORDER BY m.ingested_at DESC, t.name LIMIT 20"
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c "SELECT time, value FROM measurement JOIN tag USING (tag_id) WHERE name = 'MotorTemperature' ORDER BY time DESC LIMIT 20"
   ```

   Expected: measurements for the seven seeded tags; counts/source times advance
   when repeated after about ten seconds. Quality values are 0/1/2. OPC PLC can
   deliberately generate Bad/out-of-range values, which remain stored. PostgreSQL
   can display times in its session timezone; `timestamptz` preserves the UTC instant.
   Source time can repeat for unchanged values, so insert counts can be smaller
   than staged counts. The unique constraint keeps one row per tag/source time.

5. Inspect [raw messages in Kafka UI](http://localhost:8080/ui/clusters/local/all-topics/plant.telemetry.raw/messages?limit=100&mode=LATEST)
   and [consumer groups](http://localhost:8080/ui/clusters/local/consumer-groups).
   Expect group `transformer` to own all six partitions with one active instance;
   lag should catch up under this small load. A raw batch id should match stored
   rows unless its sample identities already existed. The DLQ may stay empty with
   the seven configured tags; the unknown-tag paths are exercised by container tests.

6. Stop Bridge first, allow its final batches to be stored, then stop Transformer
   with Ctrl+C. Confirm both terminal prompts return. Run regression/spec checks:

   ```powershell
   dotnet test --no-build -c Release
   npx -y @fission-ai/openspec@1.14.3 validate --all --strict
   npx -y @fission-ai/openspec@1.14.3 list
   ```

   Expected: 103 tests passed with no skips, nine OpenSpec items valid, and M3
   progress 12/14. Group 6 still owns removing the empty-test exit-code exception
   and recording the reviewer's final live-trend acceptance. Grafana dashboards
   are M8 work; no Grafana change is needed for this group.

## Checkpoint

On 2026-10-18, Release build passed with zero warnings/errors. All 103 regression
cases passed with no failures/skips: 55 Bridge unit cases, 34 Transformer unit
cases and 14 integration cases (2m 11.707s overall). All nine OpenSpec items passed
strict validation. Both group 5 tasks are checked; overall progress is 12/14.
All database mutations during agent verification used disposable test containers.
The agent did not launch standalone local Bridge/Transformer processes or change
the local Compose schema/data.

Await reviewer approval or feedback before group 6. M3 remains In progress.
