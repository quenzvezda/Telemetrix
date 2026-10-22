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

### CI readiness follow-up

The first CI attempt timed out in the pre-existing OPC UA repeated-restart test's
two-minute budget. No OPC UA source code changed in M4. Both OPC UA cases passed
when rerun in isolation locally (2m 46.723s total), and both passed on the next CI
attempt. No OPC UA runtime fix is claimed from that observation.

That next CI attempt exposed a separate consumer-group readiness failure:
`DescribeConsumerGroupsAsync` threw before the expected group became stable.
`ConsumerGroupReadiness` now shares the bounded polling logic between the old
consumer test and the M4 rebalance test. It retries only group-not-found,
coordinator-loading, coordinator-unavailable, not-coordinator and local request-timeout responses;
other errors still fail immediately and include their codes in test output.
The deterministic regression failed before the change and passed afterwards;
the authorization-error case proves permanent failures are not swallowed.
The real two-consumer/takeover and handler-failure tests also passed afterwards.
No tests were skipped, no membership assertions were removed, and no production
retry or delivery behavior changed in this follow-up.

The added error-code diagnostic identified `Local_TimedOut` in CI's first
10-second admin describe request. A timeout of one request is now retried inside
the existing overall test deadline. Its exact error was added to the regression
cases, observed failing before that addition to the retry predicate, then passing.
The complete local suite passed 136 cases before this last additional regression
case; the three readiness regression cases all passed afterwards.

### Final CI evidence (task 5.4)

[GitHub Actions run 39730425435](https://github.com/quenzvezda/Telemetrix/actions/runs/39730425435)
passed on commit `4f2bc0f`: all three jobs (`build-test`, `delivery-review`,
`openspec`) succeeded. Release build had zero warnings/errors; all 137 tests passed,
with zero failures/skips, in 1m 34.210s. The readiness follow-up added three test
cases, bringing the integration test project's total to 22; the Bridge and
Transformer unit totals remain 56 and 59 respectively.

The CI kill check observed 440 measurements while the source was still running,
killed Transformer with exit 137, then verified all 2,000 batches / 20,000 samples
and lag 0 after restart. The script finished successfully and cleaned its isolated
resources. These results cover the final runtime and test changes.

All 13 tracked M4 tasks are complete. Milestone status is **In review** and the
change remains active in [draft PR #1](https://github.com/quenzvezda/Telemetrix/pull/1).
Final reviewer acceptance, spec sync/archive and merge have not been performed.

## Commit map

| Area | Commits |
| --- | --- |
| D4 / M4 planning | `f166e96` |
| Group 1: producer durability | `944da79` |
| Group 2: consumer config, commits, retry, rebalance | `2e5d05b`, `d0b374d`, `5efa21c`, `081d23b` |
| Group 3: DLQ settings, validation and continuation | `6c64b61`, `97ee97b`, `8316629` |
| Group 4: ordering | `f3ed9b5` |
| Group 5: replay, crash hooks and real SIGKILL harness | `2a16e19`, `a8dd8a9`, `6d7cd38` |
| CI group-readiness regressions | `75d4e2a`, `4f2bc0f` |

The final documentation commit records task 5.4 and the review status. Use the
commands below for the full current commit list and diff.

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
