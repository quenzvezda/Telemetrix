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

Commits `8b1e8ce`, `c62699f`, `1f85342`.

The repeated OPC PLC restart integration test passed for explicit and browsed
tags (two cases). Cancellation during recovery also completed successfully.
Backoff unit tests prove doubling delays, 20% jitter bounds, a 15-second cap and
injected-clock cancellation. Review `OpcUaSourceAdapter`, `OpcUaSessionRecovery`
and `ReconnectBackoff`.

## Group 4: plant events

Commits `18fe5ce`, `9652dda`, `dcab062`.

The shared event JSON tests passed for lost, restored and gap types, including
UTC normalization. Four disposable Kafka topic tests passed for raw/events
creation and preserving existing configurations. The agent also started only the
previously stopped local Kafka/Kafka UI services, ran Bridge with a dedicated
ignored buffer path, and inspected `plant.events` in Kafka UI: 3 partitions and
`retention.ms=2592000000` (30 days). Those services were stopped afterwards;
existing local topics and retained data were preserved. The new empty events
topic remains as the Bridge-owned M5 topic.

Event tracking unit tests use an interrupted fake source and verify one lost →
restored → gap sequence per asset, exact source-timestamp gap bounds, deduplicated
transition callbacks and no invented samples for either source failure or buffer
capacity. The Bridge unit suite reached 75 passing tests. The repeated OPC PLC
restart test also passed with event tracking enabled for both tag modes; every
restart asserts all tags and ordered gaps resume within 30 seconds.

## Group 5: outage and restart verification

Commits `fb50055`, `bd08f73`, `1eeb7d0`; queue/readiness refinements are in
`f7764a8`, `ed360ac`, and test isolation in `68d09c6`.

The disposable Kafka pause/restart integration test passed: two batches and a
plant event were persisted, the Bridge host was stopped and recreated with the
same SQLite file while Kafka remained paused, and a third batch joined the FIFO.
After unpause, all three batch identities and the event arrived, raw values were
ordered 1 → 2 → 3, and the first persisted payload was byte-identical on replay.

The production-host OPC UA → Bridge → Kafka test also passed: stop the OPC PLC
container, observe lost events for both assets, restart, then consume all seven
fresh tags and six keyed plant events within 30 seconds of server readiness.
This uses the real shared publication path and Kafka, without a fake event
publisher. Additional source tests cover initial server unavailability and two
capacity pause/resume cycles: no samples are collected while paused, collection
resumes automatically, and every transition carries `buffer-capacity`.

The first Linux CI run exposed a recovery case where the SDK reported a replaced
session with one subscription but fresh tags did not resume within 30 seconds.
The Bridge now treats a replaced SDK session as a request to rebuild the source
cycle and all monitored items from configuration before publishing restored.
The 30-second acceptance deadline remains unchanged.

## Repeatable review checks

Run from the repository root with Docker running:

```powershell
dotnet build -c Release
dotnet test --no-build -c Release
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
```

Expected: no build warnings/errors, all unit/integration cases pass, and all
active changes and main specs pass strict validation.

Run the complete NFR-02 check separately (allow at least 31 minutes plus build):

```powershell
./scripts/Test-M5EdgeOutage.ps1
```

The deterministic source runs the real Bridge pipeline at seven samples/s with
default batching. The script measures at least 1,800 seconds with Kafka stopped,
then waits for source completion, an empty SQLite buffer and zero Transformer
lag. Expected: `M5_SOURCE_DONE: ticks=1840, sent=12880, buffer=0` and
`M5_PASS: sent=12880, stored=12880, tags=7, lag=0`, followed by cleanup success.
The extra 40 source seconds span startup and recovery. A 15-second smoke check
uses `-OutageSeconds 15` and expects 385 measurements; CI runs that shorter check,
which does not replace the full acceptance run. Each run creates a fresh named
project and cleans up only its own containers, network, images and database/buffer
volumes. No normal local database or `.env` credential is used.

For a focused NFR-04 check:

```powershell
dotnet test tests/Telemetrix.Integration.Tests -c Release --filter FullyQualifiedName~PlantEventDeliveryTests
```

Expected: one passing production-host test. It consumes lost events for both
assets, restarts OPC PLC, and requires all seven fresh tags plus restored/gap
events on Kafka within 30 seconds of server readiness. Additional
`OpcUaRecoveryTests` cases cover repeated restarts, browse, initial unavailability
and actual subscription pause/resume at capacity.

For interactive local inspection, start the normal stack and Bridge using the
README instructions, then run this in another terminal:

```powershell
docker compose -f deploy/docker-compose.yml restart opc-plc
```

Inspect [plant events in Kafka UI](http://localhost:8080/ui/clusters/local/all-topics/plant.events/messages).
Each affected asset has `ConnectionLost` → `ConnectionRestored` → `Gap`, with
UTC bounds from collected source timestamps. Existing topics keep their settings.
Grafana remains at <http://localhost:3000>; gap dashboards belong to M8.

Read-only SQL equivalents of the acceptance verifier:

```sql
SELECT t.asset_id, t.name, count(*) AS measurements
FROM measurement m JOIN tag t USING (tag_id)
GROUP BY t.asset_id, t.name
ORDER BY t.asset_id, t.name;

SELECT count(*) AS measurements,
       count(DISTINCT (tag_id, time)) AS distinct_measurements
FROM measurement;
```

In the fresh full acceptance database, each of the seven tag counts is 1,840
and both totals are 12,880. Normal local database counts depend on retained
history; the script executes verification before removing its disposable database.

## Acceptance evidence, 2026-10-22

| Check | Recorded result |
| --- | --- |
| Release CI with the final outage harness | 164 tests passed, zero failed/skipped; build/test, strict OpenSpec, SIGKILL and edge-outage jobs all passed in [run 39782779905](https://github.com/quenzvezda/Telemetrix/actions/runs/39782779905) |
| Production M4 SIGKILL regression | Exit 137 while source still active; 20,000 sent/stored, 2,000 batches, lag zero |
| Short M5 Kafka outage | 385 sent/stored, seven tags, empty buffer and zero lag |
| Full NFR-02 Kafka outage | Kafka stopped for 1,801.140 measured seconds; 12,880 sent/stored, seven tags, empty buffer and zero lag |
| NFR-04 and capacity recovery | Five focused integration cases passed with the unchanged 30-second deadline |

The full outage used disposable project `telemetrix-m5-b2a38c10ff39`. The source
reported progress throughout the outage and finished all 1,840 ticks. Verification
returned `M5_PASS: sent=12880, stored=12880, tags=7, lag=0`; cleanup succeeded.
The source uses the real Bridge batching/publication/replay pipeline with a
deterministic default-load adapter; the separate production-host test covers the
real OPC UA adapter and plant events. Normal local Kafka/database volumes were
not reset or used for the outage test.

All 15 tracked M5 tasks are complete. The milestone remains **In review**, with
[draft PR #2](https://github.com/quenzvezda/Telemetrix/pull/2) ready for reviewer
acceptance. Archive and merge follow that acceptance; no open M5 decision remains.

## Operational limits

Keep the SQLite file on persistent writable storage, one file per Bridge
instance. The quota counts serialized payloads, not SQLite/WAL filesystem usage.
At capacity the Bridge preserves accepted batches and stops subscribing; source
changes during that pause are unavailable and reported as a gap. Abrupt death
before an in-memory delivery has spilled to SQLite still has an in-memory loss
window; restart guarantees here cover persisted entries. Replay after ambiguous
acknowledgement can duplicate Kafka delivery; measurement identity remains
duplicate-safe through ADR-0003. See ADR-0013 before changing these settings.
