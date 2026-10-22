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
