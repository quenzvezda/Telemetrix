# M1 group 2 review

Change: `add-local-environment`. Branch: `m1-add-local-environment`.
All six component checks passed on 2026-10-18. The changes record verification
evidence and the OPC UA node inventory; no service code or infrastructure
configuration needed changes in this group.

Review the group 2 checkboxes and evidence in
[tasks.md](../openspec/changes/add-local-environment/tasks.md), and the
[OPC UA node inventory](m1-opcua-verification.md).

The following PowerShell commands run from the repository root. They reproduce
the checks for review; the existing stack can remain running throughout.

Show the task commits and the complete group 2 documentation diff:

```powershell
git log --oneline 1e05964..HEAD
git diff 1e05964..HEAD -- openspec/changes/add-local-environment/tasks.md docs/m1-opcua-verification.md docs/m1-group2-review.md
```

## 2.1 OPC UA

1. In UaExpert, add `opc.tcp://localhost:50000` with security None and Anonymous
   authentication. Accept replacing the container hostname with `localhost`
   when prompted, and accept the local OPC PLC certificate.
2. Connect, browse `Objects/OpcPlc/Telemetry`, and drag variables into Data
   Access View.
3. Verify changing values, source timestamps, data types and status codes.
   Compare the NodeIds with the recorded inventory. The reviewer already
   completed this check; the inventory includes all 12 displayed nodes.

## 2.2 Kafka UI

1. Open http://localhost:8080 and select the `local` cluster.
2. Create `scratch.test` with one partition and replication factor one.
3. Produce `{"message":"hello telemetrix"}` and read it on the Messages tab.
4. Delete `scratch.test` and refresh the topic list.

Expected: the message is readable before deletion, and the topic is absent
afterward. The reviewer supplied the message screenshot and confirmed deletion.

## 2.3 Missing Kafka topic

List topics before the attempt and ensure `m1.missing-topic.20261018` is absent:

```powershell
docker compose -f deploy/docker-compose.yml exec -T kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --list
```

Attempt a synchronous send, then list topics again:

```powershell
Write-Output m1-auto-create-probe | docker compose -f deploy/docker-compose.yml exec -T kafka /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --topic m1.missing-topic.20261018 --sync --producer-property max.block.ms=5000 --producer-property request.timeout.ms=3000 --producer-property delivery.timeout.ms=5000
docker compose -f deploy/docker-compose.yml exec -T kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --list
```

Expected: `UNKNOWN_TOPIC_OR_PARTITION`, a metadata timeout and producer exit 1;
the topic remains absent. These were the observed results.

## 2.4 TimescaleDB

```powershell
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c "SELECT count(*) FROM tag"
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c "SELECT hypertable_schema, hypertable_name FROM timescaledb_information.hypertables"
```

Expected and observed: count `7`, and `public | measurement` in the hypertable
query. Both queries are read-only.

## 2.5 Grafana

Open http://localhost:3000, sign in, and open **Connections > Data sources >
TimescaleDB**. Use the **Test** button at the bottom of the provisioned data
source page.

Expected and reviewer-confirmed: **Database Connection OK**. The provisioned
page uses **Test** for the connection check described as **Save & test** in the
task checklist.

## 2.6 MQTT

In terminal A, start a subscriber that exits after one message or 30 seconds:

```powershell
docker compose -f deploy/docker-compose.yml exec -T mosquitto mosquitto_sub -h localhost -t telemetrix/m1/smoke -C 1 -W 30 -v -d
```

Once terminal A reports `SUBACK`, publish from terminal B:

```powershell
docker compose -f deploy/docker-compose.yml exec -T mosquitto mosquitto_pub -h localhost -t telemetrix/m1/smoke -m m1-mqtt-roundtrip-20261018
```

Expected and observed in terminal A:

```text
telemetrix/m1/smoke m1-mqtt-roundtrip-20261018
```

The subscriber received the message and disconnected. The publisher succeeded.
The test did not use retained messages.

## Checkpoint

Group 2 is complete; await reviewer approval before group 3. M1 remains
**In progress** with 9 of 11 tasks complete. Remaining work is the README
local-environment documentation and the final fresh-volume milestone check.
No volume reset was performed for this review checkpoint.
