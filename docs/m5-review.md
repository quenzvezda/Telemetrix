# M5 edge resilience review

Change: `add-edge-store-and-forward`.
Branch: `m5-add-edge-store-and-forward`.
The reviewer accepted D3 and authorized autonomous implementation across all task
groups on 2026-10-22. M4 was accepted and archived in commit `a4574d1`.

## Groups 1–2: capacity and store-and-forward

Commits `cde4a7a`, `395c0dd`, `4afe192`, `00514da`, `b023bb2`, `7310901`.
Review ADR-0013, `Buffer/SqliteBuffer`, `Publishing/StoreAndForwardQueue` and the
README buffer settings. The admission semaphore never blocks replay; persisted
FIFO entries are deleted only after Kafka acknowledgement. Startup counters are
loaded from SQLite and metrics use cached counts. Source collection pauses at
80% payload usage and can resume at 50%. No batch expires or is evicted.

Verification:

```powershell
dotnet test tests/Telemetrix.Edge.Bridge.Tests --verbosity minimal
```

Recorded result after group 2: 68 tests pass, including restart/FIFO, full quota,
replay during blocked admission, metrics, options and one-hour default sizing.

For manual inspection use a dedicated buffer file, keep the same path across
restarts, and watch the `Edge buffering started` / `Edge buffer is empty` logs.
Do not delete a nonempty file. Kafka UI is at <http://localhost:8080> and Grafana
at <http://localhost:3000>; M5 does not add dashboards.
