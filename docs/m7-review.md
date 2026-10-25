# M7 MQTT input review

Status: all 13 implementation tasks are complete and verified; reviewer acceptance
and OpenSpec archive are pending. The reviewer authorized all task groups autonomously on
2026-10-25. Review this branch before archiving `add-mqtt-source` or merging it.

## What changed and why

- `Telemetrix.Contracts` defines the MQTT tag payload and quality strings using
  the existing JSON options. MQTT booleans become numeric 1/0 in `Sample`.
- `Telemetrix.Simulation` owns the unchanged pumping-line model and seven tag
  definitions. PlantSim and the separate MqttSim both reference it.
- MqttSim publishes seven retained QoS 1 samples per second and retries broker
  startup/network failures while preserving each pending source timestamp.
- Bridge selects OPC UA (default) or MQTT using `Source:Kind`. Both feed the
  existing per-asset batching, SQLite store-and-forward and Kafka publication.
- OPC UA subscriptions include source timestamp changes, keeping constant Boolean
  measurements at the simulator's 1 Hz rate for a fair protocol comparison.
- MQTT normal reconnects preserve persistent sessions; capacity pauses reset
  deliberately uncollected broker history and report the collection gap.
- Configured MQTT topic filters support standard `+` and `#` wildcards. After a
  persistent-session filter change, older subscriptions cannot admit samples
  outside the current filter.
- Malformed samples increment `bridge.mqtt.rejected`. Warnings are limited to
  one per ten seconds per adapter, including unknown quality mapped to Bad.

Read [ADR-0014](adr/0014-mqtt-payload-and-session-boundaries.md), then the payload,
mapper, decoder and adapter under `Sources/Mqtt`, followed by source registration
and the shared simulation extraction. Transformer source/configuration, database
schema, batch contract and the 500-sample/5-second defaults stay unchanged.
Use `git log --oneline main..HEAD` for the task commits and
`git diff main -- src/Telemetrix.Transformer` to verify the Transformer boundary.

## Automated verification

The Mosquitto integration tests prove persistent-session recovery, identity/value
mapping and capacity pause/resume. The production MqttSim Docker test stores all
seven tags through Bridge and Transformer, checks `source=mqtt` and the broker
endpoint on raw batches, restarts Mosquitto, and consumes ordered lost/restored/gap
events from Kafka for Pump01 and Tank01. Assertions use real Kafka and TimescaleDB.

Observed markers:

```text
M7_PIPELINE_PASS: seven tags stored from the production MqttSim container; Running=1.
M7_RESTART_PASS: seven fresh tags and six ordered durable plant.events after broker restart.
```

The final Release build passed with zero warnings/errors. All 207 tests outside
the protocol-comparison class passed together. The comparison class also passed
its 15-second check with 15 measurements for each of seven tags in both protocols
(105 MQTT and 105 OPC UA measurements). Strict OpenSpec validation passed all
12 items (active changes and main specs). The 207-test suite plus the full
600-second comparison cover all 208 tests successfully.

Two existing timing-sensitive tests were stabilized: replay cancellation may
return normally between iterations or throw inside an await, and Kafka topic
metadata/configuration can become visible shortly after creation. Exact delivery,
ordering, retention and deadline assertions remain in place.

The real 600-second protocol comparison passed on 2026-10-25 over the UTC
interval `[02:45:22.6936400, 02:55:22.6936400)`. Each protocol stored exactly
600 measurements per tag: 4,200 MQTT and 4,200 OPC UA measurements across all
seven tags, with zero observed count difference. It used two disposable
Kafka/TimescaleDB stacks and the same source-time interval for both protocols;
the assertion allows at most one sample of window-boundary difference per tag. CI's default comparison is a 15-second regression, not the
ten-minute acceptance observation.

Re-run from the repository root with Docker running:

```powershell
dotnet build -c Release
dotnet test --no-build -c Release
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
./scripts/Test-M7ProtocolComparison.ps1
```

The comparison takes about eleven minutes including setup/drain. Its XML evidence
is written to ignored `artifacts/m7-comparison.xml`; `-Seconds 15` is a short harness
check. The tests remove their own disposable containers/databases and temporary
SQLite files, without using the normal local database or `deploy/.env` credentials.

## Manual checks

### 1. Start the source and inspect its payloads

Use the existing `deploy/.env` and start the required services without resetting
their volumes:

```powershell
docker compose -f deploy/docker-compose.yml up -d --build mqttsim kafka timescaledb kafka-ui
docker compose -f deploy/docker-compose.yml exec -T mosquitto mosquitto_sub -t 'plant/#' -q 1 -v -C 70 -W 20
```

Expect seven topics under `plant/Plant01/Line01`, with one fresh sample per topic
per second after retained delivery. `Running` is JSON `true`; all timestamps are
UTC and quality is `good`. Do not run a host MqttSim with the same client id while
the compose simulator is running. The [simulator README](../src/Telemetrix.MqttSim/README.md)
documents host execution and the bad-quality switch.

### 2. Run Bridge and the existing Transformer

In one terminal:

```powershell
dotnet run --project src/Telemetrix.Edge.Bridge -c Release --no-launch-profile -- --Source:Kind=Mqtt --Buffer:Path=artifacts/m7-review/edge.db
```

Expect `MQTT subscription created at mqtt://localhost:1883 with plant/+/+/+/+`
and accepted batches for Pump01 and Tank01. In a second terminal run Transformer
using the existing secret-safe configuration instructions under
[Run the Transformer](../README.md#run-the-transformer). No MQTT-specific
Transformer setting is needed.

Open [Kafka UI](http://localhost:8080): **local > Topics > plant.telemetry.raw >
Messages**. Inspect a fresh batch: `source` is `mqtt`, `endpoint` is
`mqtt://localhost:1883`, identity matches its topic, and `Running` is numeric 1.
Port 8080 must be available for the local Kafka UI service.

If another application occupies 8080, omit `kafka-ui` from the startup command
and run a temporary UI on 18080 instead:

```powershell
docker compose -f deploy/docker-compose.yml run --rm --no-deps -p 18080:8080 kafka-ui
```

Use [the alternate Kafka UI](http://localhost:18080) for the same checks, and
stop that foreground command with Ctrl+C afterward.

### 3. Verify measurements

Run this read-only SQL against the local `telemetry` database:

```sql
SELECT t.asset_id, t.name, count(*) AS measurements,
       min(m.time) AS first_source_time, max(m.time) AS latest_source_time
FROM measurement m JOIN tag t USING (tag_id)
WHERE m.time > now() - interval '1 minute'
GROUP BY t.asset_id, t.name ORDER BY t.asset_id, t.name;

SELECT m.time, m.value, m.quality
FROM measurement m JOIN tag t USING (tag_id)
WHERE t.asset_id = 'Pump01' AND t.name = 'Running'
ORDER BY m.time DESC LIMIT 5;
```

Expect seven tags after a batch window. While running steadily each tag adds
about 60 measurements per minute. `Running` is numeric 1 and quality is 0 (Good), as defined by the existing schema.

### 4. Restart the broker

```powershell
docker compose -f deploy/docker-compose.yml restart mosquitto
```

Expect MqttSim to reconnect and the Bridge to recreate its subscription. In
Kafka UI's `plant.events` messages, filter by asset key. Each known asset has
`ConnectionLost`, `ConnectionRestored`, then `Gap` with reason `source-unavailable`
and an increasing source-time interval. Fresh measurements resume for all seven
tags. A broker restart loses its development-only in-memory session state;
ordinary Bridge/network reconnects preserve queued QoS 1 deliveries.

### 5. Exercise bounded collection and malformed input

```powershell
dotnet test tests/Telemetrix.Integration.Tests -c Release --filter FullyQualifiedName~MqttSourceTests
dotnet test tests/Telemetrix.Edge.Bridge.Tests -c Release --filter FullyQualifiedName~MqttSampleDecoderTests
```

Expect both to pass. The first fills a real SQLite buffer, verifies accepted
samples remain, resumes below the low-water mark, rejects queued pause history
and verifies ordered capacity-gap events. The second rejects/counts a malformed
flood, checks warning rate limiting and then accepts a valid sample.

Stop host services with Ctrl+C, then `docker compose -f deploy/docker-compose.yml
stop mqttsim` when finished. Do not archive the change until reviewer acceptance.
