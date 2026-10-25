# Plant Telemetry Pipeline: OPC UA → Kafka → DB (C#)

Oct 17, 2026 · @Ripan

## Overview and learning goals

You will build a four-stage telemetry pipeline in C#: a simulated plant exposes sensor values over OPC UA, a bridge collects them in time-series batches and publishes to Kafka, a transformer cleans and enriches them, and a time-series database stores the result. No microcontroller or embedded code is involved; the "plant" is software that behaves like a PLC on the network, so every line you write is ordinary .NET.

The stack mirrors what real factories run: OPC UA on the shop floor, Kafka as the plant-to-IT backbone, and a historian-style database behind it. MQTT is supported as a second input so you can compare the two protocols on the same data.

By the end you should be able to:

- Browse an OPC UA address space and subscribe to live values with sampling and publishing intervals.
- Batch and buffer telemetry at the edge so nothing is lost when the network or Kafka is down.
- Produce and consume Kafka messages with keys, partitions, consumer groups and manual offset commits.
- Design an idempotent write path so restarts never create duplicate rows.
- Store and query time series efficiently, and watch the whole thing in a dashboard.

## Architecture

Data moves one way, from plant to database, through Kafka. Putting Kafka in the middle means the edge keeps collecting when the database is down, and the database side can be rebuilt or replayed without touching the plant.

&#91;embedded content: pipeline architecture · 5 layers, 2 services you write\]

The life of one sample:

1. The simulator updates `Pump01.MotorTemperature` once a second.
2. The bridge's OPC UA subscription is notified of the change (sampling 500 ms, publishing 1 s) and turns it into a `Sample` with source timestamp and status code.
3. The batcher adds it to Pump01's open batch, which flushes at 500 samples or 5 seconds.
4. The producer writes the batch to `plant.telemetry.raw` with key `Pump01`. If Kafka is unreachable, the batch goes to the SQLite buffer and is replayed in order later.
5. The transformer consumes the batch, validates it (failures go to the DLQ), maps quality, checks ranges and resolves tag ids.
6. It bulk-copies the rows into a staging table, inserts them with `ON CONFLICT DO NOTHING`, and only then commits the Kafka offset.
7. Grafana queries the hypertable and the 1-minute aggregate to draw the trend.

## Components

Five services, each a separate process, plus a dashboard. Only the bridge and the transformer are required C# code; the simulator can start as an off-the-shelf container and become your own code later.

### 1. Plant simulator (OPC UA server)

Stands in for the PLCs. Two options, in this order:

1. **Start with Microsoft OPC PLC** (a ready-made Docker image). It exposes changing values such as sine waves, ramps and random spikes on port 50000. Zero code; lets you focus on the client side first.
2. **Then write `PlantSim` yourself** with the OPC Foundation .NET server SDK. You model a real line (pumps, tank, motor) with physically plausible values: temperature drifts with load, a pump trips and its flow drops to zero, a sensor occasionally reports Bad quality. Writing the server side is where OPC UA's information model really clicks.

For MQTT, a small `MqttSim` console app publishes the same tags to Mosquitto, so both protocols carry identical data.

### 2. Edge bridge (`Edge.Bridge`)

A .NET Worker Service, the most important piece to learn. It:

- Connects to the OPC UA server, browses or reads a tag list from config, and creates one **subscription** with a **monitored item** per tag (sampling 500 ms, publishing 1,000 ms).
- Receives data-change notifications and turns each into a `Sample` (tag, value, source timestamp, status code).
- Pushes samples into an in-memory `Channel<Sample>`; a batcher drains it and emits a **batch per asset** when it reaches 500 samples or 5 seconds, whichever comes first.
- Produces each batch to Kafka with the asset id as the message key, so one asset's data stays ordered in one partition.
- Spills batches to a local SQLite buffer when Kafka is unreachable and replays them in order when it returns (store-and-forward).
- Reconnects automatically when the OPC UA session drops, and records the gap.

The input side sits behind an `ISourceAdapter` interface with two implementations, `OpcUaSourceAdapter` and `MqttSourceAdapter`, so the batching and Kafka code is shared.

### 3. Kafka

The durable backbone between plant and IT. A single-node Kafka 4.x broker in KRaft mode (no ZooKeeper) is enough locally. It decouples the bridge from the database: if the transformer is down for an hour, data waits in the topic and nothing is lost.

### 4. Transformer (`Transformer`)

A second Worker Service in its own consumer group. For each batch it:

- Validates the envelope against the schema version; malformed messages go to a dead-letter topic.
- Drops or flags Bad-quality samples, converts units to a canonical set, and rejects out-of-range values.
- Enriches samples with asset metadata (line, area, engineering unit) from the database.
- Bulk-writes to the database, then commits the Kafka offset. Writes are idempotent, so a replay after a crash inserts nothing twice.
- Optionally republishes cleaned data to `plant.telemetry.clean` for other consumers.

### 5. Database (TimescaleDB)

PostgreSQL with the TimescaleDB extension. Raw measurements go into a hypertable partitioned by time; a continuous aggregate maintains 1-minute rollups (min, max, avg) for dashboards. Plain PostgreSQL works too, but you would lose automatic partitioning and rollups.

### 6. Observability

Grafana reads TimescaleDB for trend charts. Each service logs structured JSON and exports OpenTelemetry metrics: samples received, batches produced, buffer depth, consumer lag, rows written. A Kafka web UI lets you inspect topics and messages by hand.

## Data model

One identity runs end to end: `plant / line / asset / tag`. The OPC UA NodeId, the Kafka message key and the database foreign keys all derive from it, so any value can be traced back to its sensor.

### Simulated plant: a small pumping line

OPC UA address space under `Objects/Plant01/Line01/`, namespace 2. Your own `PlantSim` exposes these; with OPC PLC you map its generic nodes to these names in the bridge config.

| Asset | Tag | NodeId | Type | Unit | Normal range | Behaviour |
| --- | --- | --- | --- | --- | --- | --- |
| Pump01 | Running | `ns=2;s=Line01.Pump01.Running` | Boolean | — | — | Trips randomly about once an hour |
| Pump01 | FlowRate | `ns=2;s=Line01.Pump01.FlowRate` | Double | m³/h | 40–60 | Drops to 0 when not running |
| Pump01 | DischargePressure | `ns=2;s=Line01.Pump01.DischargePressure` | Double | bar | 3.5–5.0 | Follows flow with noise |
| Pump01 | MotorTemperature | `ns=2;s=Line01.Pump01.MotorTemperature` | Double | °C | 45–80 | Rises slowly under load |
| Pump01 | Vibration | `ns=2;s=Line01.Pump01.Vibration` | Double | mm/s | 0.5–4.5 | Occasional spikes |
| Tank01 | Level | `ns=2;s=Line01.Tank01.Level` | Double | % | 20–90 | Fills and drains with the pump |
| Tank01 | Temperature | `ns=2;s=Line01.Tank01.Temperature` | Double | °C | 15–30 | Sometimes reports Bad quality |

Scale it by cloning the line: 10 lines × 7 tags at 1 Hz is 70 samples/s, enough to see batching and partitioning work.

### Kafka topics

| Topic | Key | Partitions | Retention | Producer → consumer |
| --- | --- | --- | --- | --- |
| `plant.telemetry.raw` | assetId | 6 | 7 days | Bridge → Transformer |
| `plant.telemetry.clean` | assetId | 6 | 7 days | Transformer → any future consumer (optional) |
| `plant.telemetry.dlq` | original key | 1 | 30 days | Transformer → you, for inspection |
| `plant.events` | assetId | 3 | 30 days | Bridge → Transformer (connection lost/restored, gaps) |

### Batch message (`plant.telemetry.raw`)

JSON first for readability; switch to Avro or Protobuf with a schema registry as a stretch goal. `schemaVersion` lets the transformer reject or upgrade old formats.

```json
{
  "schemaVersion": 1,
  "batchId": "0192f6c1-6d2e-7c3a-9b11-5e2f0a7d4c88",
  "source": "opcua",
  "endpoint": "opc.tcp://plantsim:4840",
  "plantId": "Plant01",
  "lineId": "Line01",
  "assetId": "Pump01",
  "createdAt": "2026-10-17T09:15:05.012Z",
  "samples": [
    {
      "tag": "MotorTemperature",
      "value": 71.3,
      "sourceTimestamp": "2026-10-17T09:15:01.000Z",
      "serverTimestamp": "2026-10-17T09:15:01.004Z",
      "statusCode": 0
    },
    {
      "tag": "FlowRate",
      "value": 52.8,
      "sourceTimestamp": "2026-10-17T09:15:01.000Z",
      "serverTimestamp": "2026-10-17T09:15:01.004Z",
      "statusCode": 0
    }
  ]
}
```

`statusCode` is the raw OPC UA StatusCode (0 = Good); the transformer maps it to Good, Uncertain or Bad. Sample values use `double?`; source adapters convert booleans to 1/0, and that numeric value travels through batches into storage. Strings are out of scope for v1 (ADR-0010).

### Database tables

```sql
CREATE TABLE asset (
  asset_id   text PRIMARY KEY,          -- 'Pump01'
  plant_id   text NOT NULL,
  line_id    text NOT NULL,
  asset_type text NOT NULL              -- 'pump', 'tank'
);

CREATE TABLE tag (
  tag_id     serial PRIMARY KEY,
  asset_id   text NOT NULL REFERENCES asset,
  name       text NOT NULL,             -- 'MotorTemperature'
  unit       text,
  min_valid  double precision,
  max_valid  double precision,
  UNIQUE (asset_id, name)
);

CREATE TABLE measurement (
  time        timestamptz NOT NULL,     -- OPC UA source timestamp
  tag_id      int NOT NULL REFERENCES tag,
  value       double precision,
  quality     smallint NOT NULL,        -- 0 good, 1 uncertain, 2 bad
  batch_id    uuid NOT NULL,
  ingested_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tag_id, time)                 -- makes replays idempotent
);
SELECT create_hypertable('measurement', 'time');

CREATE MATERIALIZED VIEW measurement_1m
WITH (timescaledb.continuous) AS
SELECT time_bucket('1 minute', time) AS bucket, tag_id,
       min(value), max(value), avg(value), count(*)
FROM measurement WHERE quality = 0
GROUP BY bucket, tag_id;
```

The transformer writes with binary `COPY` into a temporary staging table, then `INSERT … SELECT … ON CONFLICT DO NOTHING` into `measurement`. That keeps bulk-insert speed and still ignores duplicates from a replayed batch.

## Functional requirements

IDs let you reference requirements from tests and commit messages. "Must" items define a working v1; "Should" items come once v1 runs end to end.

| ID | Component | Requirement | Priority |
| --- | --- | --- | --- |
| FR-01 | Simulator | Expose the 7 tags in the data model over OPC UA, updating at 1 Hz with realistic noise and correlations | Must |
| FR-02 | Simulator | Inject faults on demand or at random: pump trip, Bad-quality status, value spike, server restart | Should |
| FR-03 | Simulator | Publish the same tags over MQTT to topic `plant/Plant01/Line01/{asset}/{tag}` | Should |
| FR-10 | Bridge | Load endpoint, security mode and tag list from `appsettings.json`, overridable by environment variables | Must |
| FR-11 | Bridge | Subscribe to every configured tag with configurable sampling and publishing intervals and queue size | Must |
| FR-12 | Bridge | Optionally discover tags by browsing a start node instead of listing them | Should |
| FR-13 | Bridge | Group samples into one batch per asset, flushed at N samples or T seconds (defaults 500 / 5 s) | Must |
| FR-14 | Bridge | Produce batches to `plant.telemetry.raw`, keyed by assetId, with a unique batchId | Must |
| FR-15 | Bridge | Buffer batches to local disk when Kafka is unreachable and replay them in original order on recovery | Must |
| FR-16 | Bridge | Reconnect to the OPC UA server with exponential backoff and publish connection-lost and restored events | Must |
| FR-17 | Bridge | Ingest from MQTT through the same batching and producing path | Should |
| FR-20 | Transformer | Consume `plant.telemetry.raw` in consumer group `transformer` with auto-commit disabled | Must |
| FR-21 | Transformer | Reject messages that fail schema validation to `plant.telemetry.dlq` with the error reason in a header | Must |
| FR-22 | Transformer | Map OPC UA status codes to Good / Uncertain / Bad; mark values outside the tag's valid range as Bad | Must |
| FR-23 | Transformer | Resolve asset and tag names to database ids; send unknown-tag samples to the DLQ by default, with automatic tag creation as a configurable opt-in (ADR-0011) | Must |
| FR-24 | Transformer | Write samples in bulk, then commit offsets; never commit before the write succeeds | Must |
| FR-25 | Transformer | Republish cleaned batches to `plant.telemetry.clean` | Should |
| FR-30 | Database | Store raw samples in the `measurement` hypertable and reject duplicate (tag, time) pairs | Must |
| FR-31 | Database | Maintain 1-minute rollups as a continuous aggregate | Should |
| FR-32 | Database | Drop raw data older than 30 days and keep rollups for 1 year (retention policies) | Should |
| FR-40 | Dashboard | Show live trends per tag, current pump status, and data gaps | Should |
| FR-41 | All | Expose `/health` endpoints and metrics: samples in, batches out, buffer depth, consumer lag, rows written | Must |

## Non-functional requirements

The central guarantee is **no data loss and no duplicate rows**, even when any single service is killed mid-batch. Kafka gives at-least-once delivery; the unique `(tag_id, time)` key turns that into effectively-once in the database.

| ID | Area | Requirement | How to verify |
| --- | --- | --- | --- |
| NFR-01 | Delivery | Every sample reaching the bridge is stored exactly once in `measurement` | Count rows vs. samples sent by the simulator over 1 hour |
| NFR-02 | Resilience | Kafka down for 30 min: bridge buffers to disk, then drains with zero loss | `docker compose stop kafka`, wait, start, compare counts |
| NFR-03 | Resilience | Transformer killed mid-batch: on restart, no gaps and no duplicates | `docker kill transformer` during load, then compare counts |
| NFR-04 | Resilience | OPC UA server restarted: bridge reconnects within 30 s and logs the gap | Restart the simulator container, read `plant.events` |
| NFR-05 | Ordering | Samples for one asset are processed in source-timestamp order | Key by assetId; assert monotonic timestamps per tag in a test |
| NFR-06 | Latency | Sensor change to row in database: p95 under 10 s at default batch settings | Compare `time` with `ingested_at` |
| NFR-07 | Throughput | Sustain 5,000 samples/s on a laptop without growing consumer lag | Scale the simulator up; watch lag in the Kafka UI |
| NFR-08 | Producer | Kafka producer runs with `acks=all` and idempotence enabled | Config review |
| NFR-09 | Security | Dev runs with OPC UA security mode None; a later milestone uses Basic256Sha256 Sign & Encrypt with trusted certificates | Connection fails with an untrusted cert |
| NFR-10 | Security | No credentials in source control; secrets via environment variables or user-secrets | Repo scan |
| NFR-11 | Operability | Whole stack starts with one `docker compose up` | Fresh clone test |
| NFR-12 | Observability | Structured logs with batchId as a correlation id across bridge and transformer | Trace one batch through both logs |
| NFR-13 | Testability | Unit tests for batching, mapping and validation; integration tests against real Kafka and Postgres in containers | `dotnet test` green in CI |

**Time handling:** store everything in UTC, use the OPC UA *source* timestamp as the measurement time, and keep server and ingest timestamps for diagnosing clock drift.

## Tech stack

Everything is free and runs in Docker on one machine. Use the current .NET LTS (.NET 10) and the Worker Service template (`dotnet new worker`) for both services.

| Concern | Choice | Notes |
| --- | --- | --- |
| OPC UA client and server | [OPCFoundation.NetStandard.Opc.Ua](https://github.com/OPCFoundation/UA-.NETStandard) (`.Client`, `.Server` packages) | The official reference stack; its sample apps are worth reading |
| Ready-made OPC UA simulator | [Microsoft OPC PLC](https://github.com/Azure-Samples/iot-edge-opc-plc) | Docker image, port 50000 |
| OPC UA browsing tool | UaExpert (free desktop client) | Inspect nodes and values before writing code |
| MQTT client | [MQTTnet](https://github.com/dotnet/MQTTnet) | Broker: Eclipse Mosquitto in Docker |
| Kafka client | [Confluent.Kafka](https://github.com/confluentinc/confluent-kafka-dotnet) | Wraps librdkafka; idempotent producer, manual commits |
| Kafka broker | Apache Kafka 4.x, single node, KRaft mode | Image `apache/kafka` |
| Kafka UI | Kafka UI (kafbat) or Redpanda Console | Browse topics, messages, consumer lag |
| Database | TimescaleDB on PostgreSQL | Image `timescale/timescaledb` |
| Database client | Npgsql | Binary `COPY` for bulk writes; Dapper for reads |
| Edge buffer | SQLite via Microsoft.Data.Sqlite | Ordered on-disk queue for store-and-forward |
| Retries | Microsoft.Extensions.Resilience (Polly) | Backoff for reconnects and DB writes |
| Logs and metrics | Microsoft.Extensions.Logging (JSON console), OpenTelemetry | Optional Prometheus scrape |
| Dashboard | Grafana | PostgreSQL data source |
| Tests | xUnit, Testcontainers for .NET | Real Kafka and Postgres in integration tests |

### OPC UA or MQTT for the plant side?

Use OPC UA as the primary input and MQTT as the comparison. They solve different problems, and many real plants use both: OPC UA to talk to PLCs, MQTT to fan data out from the edge.

| Aspect | OPC UA | MQTT |
| --- | --- | --- |
| Model | Client/server; the client subscribes to nodes on the device | Publish/subscribe through a broker |
| Data semantics | Built in: types, units, status codes, timestamps, browsable hierarchy | None; topic names and payload format are your convention (Sparkplug B adds some) |
| Typical place | PLCs and machines on the shop floor | Edge gateways to cloud or IT |
| Security | Certificates, signing and encryption per session | TLS plus username/password or client certs |
| Learning value here | Subscriptions, quality codes, reconnection, information modelling | Lightweight messaging, QoS levels, retained messages |
| C# library | OPC Foundation .NET Standard stack | MQTTnet |

## Solution layout and local environment

One repository, one solution, one compose file. Shared message contracts live in their own project so the bridge and transformer can never disagree on the schema.

```text
PlantTelemetry/
├─ PlantTelemetry.sln
├─ src/
│  ├─ Contracts/           # TelemetryBatch, Sample, PlantEvent records + JSON options
│  ├─ PlantSim/            # your own OPC UA server (milestone 5)
│  ├─ MqttSim/             # MQTT publisher (milestone 6)
│  ├─ Edge.Bridge/
│  │  ├─ Sources/          # ISourceAdapter, OpcUaSourceAdapter, MqttSourceAdapter
│  │  ├─ Batching/         # Channel<Sample> + size/time batcher
│  │  ├─ Buffer/           # SQLite store-and-forward queue
│  │  └─ Publishing/       # Kafka producer
│  └─ Transformer/
│     ├─ Consuming/        # consumer loop, manual commits
│     ├─ Pipeline/         # validate → map quality → range check → enrich
│     └─ Storage/          # COPY to staging + INSERT ON CONFLICT
├─ tests/
│  ├─ Bridge.Tests/
│  ├─ Transformer.Tests/
│  └─ Integration.Tests/   # Testcontainers: Kafka + TimescaleDB
└─ deploy/
   ├─ docker-compose.yml
   ├─ sql/init.sql         # tables, hypertable, aggregate, seed assets/tags
   └─ grafana/             # provisioned data source + dashboard JSON
```

### docker-compose.yml (starting point)

Infrastructure first; add `bridge` and `transformer` services once they run from your IDE. From the host, connect to Kafka on `localhost:29092`; containers use `kafka:9092`.

```yaml
services:
  opc-plc:
    image: mcr.microsoft.com/iotedge/opc-plc:latest
    command: --pn=50000 --autoaccept   # add simulation flags from the OPC PLC README
    ports: ["50000:50000"]

  mosquitto:
    image: eclipse-mosquitto:2
    ports: ["1883:1883"]

  kafka:
    image: apache/kafka:latest
    ports: ["29092:29092"]
    environment:
      KAFKA_NODE_ID: 1
      KAFKA_PROCESS_ROLES: broker,controller
      KAFKA_LISTENERS: INTERNAL://:9092,EXTERNAL://:29092,CONTROLLER://:9093
      KAFKA_ADVERTISED_LISTENERS: INTERNAL://kafka:9092,EXTERNAL://localhost:29092
      KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: INTERNAL:PLAINTEXT,EXTERNAL:PLAINTEXT,CONTROLLER:PLAINTEXT
      KAFKA_INTER_BROKER_LISTENER_NAME: INTERNAL
      KAFKA_CONTROLLER_LISTENER_NAMES: CONTROLLER
      KAFKA_CONTROLLER_QUORUM_VOTERS: 1@kafka:9093
      KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: 1
      KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR: 1
      KAFKA_TRANSACTION_STATE_LOG_MIN_ISR: 1

  kafka-ui:
    image: ghcr.io/kafbat/kafka-ui:latest
    ports: ["8080:8080"]
    environment:
      KAFKA_CLUSTERS_0_NAME: local
      KAFKA_CLUSTERS_0_BOOTSTRAPSERVERS: kafka:9092
    depends_on: [kafka]

  timescaledb:
    image: timescale/timescaledb:latest-pg17
    ports: ["5432:5432"]
    environment:
      POSTGRES_USER: telemetry
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: telemetry
    volumes:
      - ./sql/init.sql:/docker-entrypoint-initdb.d/init.sql
      - tsdata:/var/lib/postgresql/data

  grafana:
    image: grafana/grafana:latest
    ports: ["3000:3000"]
    volumes:
      - ./grafana:/etc/grafana/provisioning

volumes:
  tsdata:
```

Topics: let each service create the topics it owns at startup with the Kafka `AdminClient` in development (partitions and retention from the data model), and set `KAFKA_AUTO_CREATE_TOPICS_ENABLE: "false"` on the broker so a typo fails loudly.

## Build plan

Nine milestones, each ending in something you can see working. Get a thin path running end to end by milestone 3, then harden it; resist building the simulator or dashboards first.

1. **Environment.** Bring up OPC PLC, Kafka, Kafka UI and TimescaleDB with compose; browse OPC PLC in UaExpert.
   - Done when: you see values changing in UaExpert and can create a topic in Kafka UI.
2. **OPC UA console client.** Connect, browse, read once, then subscribe and print notifications.
   - Done when: values print every second with source timestamps and status codes, and the client resumes after you restart the OPC PLC container.
3. **Bridge to Kafka, then transformer to database.** Add the batcher and producer; write the consumer, mapping and idempotent insert.
   - Done when: rows appear in `measurement` and a `SELECT` shows the live trend. This is your first end-to-end slice.
4. **Delivery guarantees.** Manual commits after the write, duplicate-safe inserts, dead-letter topic.
   - Done when: NFR-01 and NFR-03 pass (kill the transformer under load; counts still match).
5. **Edge resilience.** SQLite store-and-forward, reconnect with backoff, connection events.
   - Done when: NFR-02 and NFR-04 pass.
6. **Your own OPC UA server (`PlantSim`).** Model the pumping line from the data model, with fault injection.
   - Done when: the bridge switches from OPC PLC to PlantSim by changing configuration only.
7. **MQTT input.** `MqttSim` plus `MqttSourceAdapter` through the same batching path.
   - Done when: the same tags land in the database from MQTT with no transformer changes.
8. **Observability and load.** Metrics, health checks, Grafana dashboard, scale-up test.
   - Done when: the dashboard shows trends, pump state and gaps, and NFR-06 and NFR-07 pass.
9. **Security.** OPC UA Sign & Encrypt with a certificate trust list; secrets out of config files.
   - Done when: an untrusted client certificate is rejected and NFR-09 and NFR-10 pass.

## Stretch goals and open decisions

Stretch goals, once milestone 9 is done:

- **Schema registry.** Move from JSON to Avro or Protobuf with a schema registry, and practise a backward-compatible schema change.
- **Separate sink.** Split the transformer into a pure transformer (raw → clean topic) and a database sink, so new consumers can reuse clean data.
- **Stream aggregation.** Compute rolling 1-minute statistics or simple alarms (vibration above 4.5 mm/s for 30 s) in the transformer and publish to `plant.alarms`.
- **Report by exception.** Add an OPC UA deadband filter so only meaningful changes are sent, and measure the traffic saved.
- **Sparkplug B.** Use the industrial MQTT payload convention and compare it with your own topic scheme.
- **Write-back.** Let an operator start or stop the simulated pump from a small web UI through an OPC UA method call.
- **Multiple brokers.** Run three Kafka brokers with replication factor 3 and observe a broker failure.

Decisions to make before or during the build:

- [x] Batch thresholds: retain 500 samples / 5 s per asset, whichever comes first; accepted after the M8 latency/throughput comparison on 2026-10-25 ([ADR-0015](adr/0015-retain-measured-batch-thresholds.md)).
- [x] Unknown tags: dead-letter by default; automatic tag creation is an explicit opt-in (ADR-0011).
- [x] Booleans and strings: nullable numeric values, booleans converted to 1/0 by source adapters, strings out of scope for v1 (ADR-0010).
- [ ] Buffer limit at the edge: how many hours of data, and what happens when it fills (drop oldest or stop subscribing)?
- [x] Defer `plant.telemetry.clean` (FR-25) to the separate-sink stretch goal (ADR-0012).
