# M3 group 4 review: Transformer pipeline

Change: `add-end-to-end-slice`, tasks 4.1–4.3.
Branch: `m3-add-end-to-end-slice`.
Status: implementation in progress.

## Task 4.1: quality and range checks

`QualityMapper` maps OPC UA severity bits 30–31 to Good (0), Uncertain (1) or
Bad (2); reserved severity values conservatively become Bad. `RangeCheck` makes
missing, non-finite or out-of-range values Bad, preserves an existing Uncertain
or Bad quality, and treats exact range boundaries as valid. Either range bound
can be absent. Values and timestamps are not changed by these functions.

Verification: all 20 Transformer unit cases passed, covering the three
severities, additional status bits, reserved severity, both bounds, inclusive
boundaries, absent bounds, missing values and non-finite input. Group 3 reviewer
acceptance and the leftover-process cleanup are recorded with this task.

## Task 4.2: tag resolution and dead-lettering

`TagResolver` loads its `(asset, tag)` cache once and refreshes on a miss. Under
the default `dead-letter` policy it separates unknown samples, preserving the
original envelope and batch id, while returning known samples with their tag
metadata in arrival order. The `create` opt-in inserts tags without unit/range;
Postgres uses parameterized SQL, `ON CONFLICT DO NOTHING RETURNING` and a re-select
when another creator wins. Catalog failures propagate rather than being
misclassified as unknown tags. Negative lookups are only cached within a batch.

Assets must already be provisioned in the schema's `asset` table: `create`
creates tags, not assets with invented types. An unprovisioned asset cannot be
resolved and its samples remain in the unknown-tag batch under either policy.

`KafkaDeadLetterPublisher` sends that subset as shared JSON with the original
batch id, asset key and `dlq.reason=unknown-tag`. The Transformer initializes
`plant.telemetry.dlq` at startup with one partition and 30-day retention, leaving
existing settings unchanged. Configuration validates `Kafka:BootstrapServers`
and `TagResolution:UnknownTagPolicy` before topic startup. The placeholder Worker
is removed. The executable initializes its owned topic; production raw consumption
and database startup loading are connected to the writer in task 5.2.

Verification: all 29 Transformer unit cases passed. Two targeted integration
cases passed: the actual TimescaleDB schema and seeded metadata, eight concurrent
tag inserts returning one identity, parameterized tag names, and Kafka DLQ
creation/repeated startup plus payload/key/header preservation. All database
mutations ran only inside disposable test containers, not the local compose DB.
