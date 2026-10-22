# M4 delivery guarantees review

Change: `add-delivery-guarantees`.
Branch: `m4-add-delivery-guarantees`.
The reviewer authorized implementation across all task groups on 2026-10-22,
with a pause only for blocking decisions. D4 is resolved in ADR-0012.

## Topic ownership (task 3.1)

M3 already implemented `DeadLetterTopicInitializer`; M4 reuses it. On 2026-10-22,
`DeadLetterTests` passed against a disposable Kafka container: first startup
creates the topic, repeated startup succeeds, and broker metadata/configuration
reports one partition and `retention.ms=2592000000` (30 days).

The agent also started only the local Compose Kafka/Kafka UI services and read
[the existing local DLQ](http://localhost:8080/ui/clusters/local/all-topics/plant.telemetry.dlq).
Its overview showed one partition; Settings showed `retention.ms=2592000000`.
No local topics, messages, offsets or database data were edited for this check.

## What to review

- `KafkaBatchPublisher.CreateConfig` pins acknowledgements, idempotence, ordered
  in-flight delivery, LZ4 compression and linger; its config test passed.
- `RawBatchConsumer` disables both automatic commit and automatic offset storage.
  It processes one batch at a time on a dedicated thread. `RawBatchProcessor`
  waits for storage and any required DLQ acknowledgement before calling back into
  `Commit(result)` on that same thread. Shutdown and failed processing do not
  advance offsets. Revoke callbacks run at the next Consume call after processing;
  the lost callback never commits.
- `TelemetryBatchHandler` retries failed storage with the shared resilience
  pipeline: exponential delay with jitter, starting at 1 s and capped at 30 s,
  until success or cancellation. Failed writes retain their offset and log the
  batch id. Tests use `FakeTimeProvider`; cancellation and two failures followed
  by success are covered. A permanently failing write intentionally stalls
  processing; consumer lag monitoring belongs to M8.
- `TelemetryJson.DeserializeValidated` distinguishes invalid JSON, schema
  violations and unsupported versions. Required constructor arguments, identities,
  nullable numeric values and samples are checked before tag resolution/storage.
  Kafka tombstones and null samples are rejected deterministically.
- `KafkaDeadLetterPublisher` retains the original key and payload for invalid
  batches and adds all six `dlq.*` headers. Error text is bounded to 1,024 UTF-8
  bytes without splitting a code point. A broker failure prevents commit.
  Unknown-tag policy remains the accepted ADR-0011 behavior. A crash after DLQ
  acknowledgement may duplicate a dead letter; this is intentional.
- ADR-0012 defers FR-25 to the separate sink. M4 adds requirements to the existing
  main capabilities; its delta sections remain ADDED because the requirements
  themselves are new.

## Automated evidence

Targeted tests passed on 2026-10-22:

| Check | Observable result |
| --- | --- |
| Consumer rebalance during an in-flight write | Two consumers store all 600 samples; all six committed offsets reach 10; removing one consumer preserves the count |
| Invalid batch between two valid batches | 20 measurements, committed offset 3, original invalid bytes/key in DLQ with reason and origin headers |
| Per-asset ordering | Two batches preserve interleaved tag order through async storage; source times per tag stay non-decreasing |
| Deliberate replay | 61 Kafka batches contain 60 unique batches / 600 samples; DB contains 600 measurements and 600 distinct `(tag_id, time)` identities |
| Crash before DB commit | No committed Kafka offset and no measurements before restart; restart stores both batches / 20 samples |
| Crash after DB commit, before offset commit | Ten durable measurements and no committed Kafka offset before restart; replay inserts zero duplicates, then stores the next ten samples |

The crash hook exists only in the integration-test writer decorator. Production
storage has no fault-injection option. The integration cases use disposable
Kafka and TimescaleDB containers, separate from the normal local stack.

## Repeat the real container crash check (task 5.3)

With Docker running, from the repository root:

```powershell
./scripts/Test-M4Delivery.ps1
```

The script builds the production Transformer and a deterministic test-only
source (`Telemetrix.DeliveryProbe`) into Linux containers. It creates a uniquely
named `telemetrix-m4-<id>` Compose project with its own database volume and no
host ports. The password is generated for this invocation and never printed or
written to a file. `deploy/.env`, the normal `telemetrix` project, its offsets
and its volumes are not used.

The source publishes 2,000 batches of ten known-tag samples using the Bridge's
actual producer configuration. Each `(tag, source timestamp)` is distinct. Once
some measurements exist and the source is still running, the script executes
`docker compose ... kill -s SIGKILL transformer`, checks exit code 137, and
restarts that same service in group `transformer`. It waits for the source to
finish and verifies all 2,000 batch identities each have ten measurements,
`SELECT count(*) FROM measurement` is 20,000, and consumer lag is zero.

Expected output includes `source still running`, exit code `137`,
`Sent 2000 batches / 20000 distinct samples`, and
`PASS: sent=20000, stored=20000, batches=2000, lag=0`.

Cleanup removes only the randomly named project created by that invocation,
including its disposable database volume. It runs on both success and failure.
This verifies Transformer crash recovery; a Kafka outage and durable edge
store-and-forward remain M5 work. It is a bounded load check, not the one-hour
load observation described in NFR-01.

### Agent execution evidence, 2026-10-22

The production Linux image passed the kill-under-load check. In review project
`telemetrix-m4-1f8d0580d30a`, the database held 1,230 measurements while the source
was still running; Transformer was then killed with exit code 137. The source
finished all 2,000 batches / 20,000 distinct samples, and verification returned
`PASS: sent=20000, stored=20000, batches=2000, lag=0`. The disposable containers,
network, database volume and locally built review images were removed afterwards;
the script exited successfully and printed its cleanup-complete confirmation.
The earlier local-topic inspection services were returned to their original
stopped state; normal local Kafka/database data were retained.

`dotnet build -c Release` passed with zero warnings/errors. The complete Release
suite passed 134 tests, zero failures/skips, in 2m 48.238s: 56 Bridge unit cases,
59 Transformer unit cases and 19 integration cases. Strict OpenSpec validation
passed all 12 current items. CI verification is tracked by task 5.4.

## Reviewer commands

```powershell
git log --oneline --reverse main..HEAD
git diff main...HEAD
dotnet build -c Release
dotnet test -c Release --no-build
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
./scripts/Test-M4Delivery.ps1
```

For the existing local topic, inspect
[Overview](http://localhost:8080/ui/clusters/local/all-topics/plant.telemetry.dlq)
and [Settings](http://localhost:8080/ui/clusters/local/all-topics/plant.telemetry.dlq/settings):
one partition and 30-day retention are expected. Grafana dashboards are M8 work;
M4 acceptance uses exact stored counts and Kafka offsets.
