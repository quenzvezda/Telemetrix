# M3 group 5 review: Transformer storage

Status: implementation in progress; reviewer checkpoint follows task 5.2.

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
