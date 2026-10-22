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

## Group 3: OPC UA recovery

The repeated OPC PLC restart integration test passed for explicit and browsed
tags (two cases). Cancellation during recovery also completed successfully.
Backoff unit tests prove doubling delays, 20% jitter bounds, a 15-second cap and
injected-clock cancellation. Review `OpcUaSourceAdapter`, `OpcUaSessionRecovery`
and `ReconnectBackoff`.

## Group 4: plant events

The shared event JSON tests passed for lost, restored and gap types, including
UTC normalization. Four disposable Kafka topic tests passed for raw/events
creation and preserving existing configurations. The agent also started only the
previously stopped local Kafka/Kafka UI services, ran Bridge with a dedicated
ignored buffer path, and inspected `plant.events` in Kafka UI: 3 partitions and
`retention.ms=2592000000` (30 days). Those services were stopped afterwards;
existing local topics and retained data were preserved. The new empty events
topic remains as the Bridge-owned M5 topic.
