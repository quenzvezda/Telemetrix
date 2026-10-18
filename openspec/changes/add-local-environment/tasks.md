# Tasks

## 1. Bring the stack up

- [x] 1.1 Copy `deploy/.env.example` to `deploy/.env`, set `POSTGRES_PASSWORD`, run `docker compose -f deploy/docker-compose.yml up -d`; verify with `docker compose -f deploy/docker-compose.yml ps` that every service is running and kafka/timescaledb are healthy
- [x] 1.2 Unset `POSTGRES_PASSWORD` and run `docker compose ... config`; verify it fails naming the variable
- [x] 1.3 Verify `git status` does not list `deploy/.env`

Verification notes (2026-10-18, Docker Desktop):

- 1.1: Reused the existing local `deploy/.env`. `docker compose -f deploy/docker-compose.yml config --quiet` and `up -d` succeeded. `ps -a` showed all six services running, with Kafka and TimescaleDB healthy. No Compose changes were needed.
- 1.2: `Test-Path Env:\POSTGRES_PASSWORD` returned `False`. On Windows, `docker compose --env-file NUL -f deploy/docker-compose.yml config --quiet` exited 1 and named the required `POSTGRES_PASSWORD`. The null env file prevents fallback to `deploy/.env`, leaving the local credentials unchanged.
- 1.3: `git status --short --untracked-files=all` did not list `deploy/.env`; `git check-ignore -v -- deploy/.env` matched `.gitignore:7:.env`; `git ls-files -- deploy/.env` returned no tracked file.

## 2. Inspect each component

- [x] 2.1 Connect UaExpert to `opc.tcp://localhost:50000` (security None), browse `Objects/OpcPlc/Telemetry`; verify values change and note the NodeIds you will map to the 7 tags in M2
- [x] 2.2 In Kafka UI (`http://localhost:8080`) create topic `scratch.test`, produce and read one message; verify, then delete the topic
- [x] 2.3 Produce to a non-existent topic with `kafka-console-producer.sh` inside the kafka container; verify it fails and no topic is created
- [x] 2.4 `docker compose ... exec timescaledb psql -U telemetry -d telemetry -c "SELECT count(*) FROM tag"`; verify 7, and that `measurement` appears in `timescaledb_information.hypertables`
- [x] 2.5 Open Grafana (`http://localhost:3000`), verify the provisioned TimescaleDB data source passes "Save & test"
- [x] 2.6 Publish and subscribe one message with `mosquitto_pub`/`mosquitto_sub` inside the mosquitto container; verify it round-trips

Verification notes (2026-10-18):

- 2.1: The reviewer connected in UaExpert, browsed `Objects/OpcPlc/Telemetry`, and confirmed changing values. The screenshots exposed 12 NodeIds and their data types; see [OPC UA verification](../../../docs/m1-opcua-verification.md) for the inventory and Windows discovery/certificate steps.
- 2.2: The reviewer's Kafka UI screenshot showed `scratch.test` containing `{"message":"hello telemetrix"}` at partition 0, offset 0. The reviewer then confirmed deleting the topic.
- 2.3: The broker's topic list was empty before and after a synchronous console-producer attempt to `m1.missing-topic.20261018`. With `max.block.ms=5000`, `request.timeout.ms=3000`, and `delivery.timeout.ms=5000`, the producer reported `UNKNOWN_TOPIC_OR_PARTITION` and exited 1 after the metadata timeout. No topic was auto-created.
- 2.4: Read-only `psql` queries inside the local `timescaledb` service returned 7 from `SELECT count(*) FROM tag` and `public | measurement` from `SELECT hypertable_schema, hypertable_name FROM timescaledb_information.hypertables`. Both queries succeeded with `ON_ERROR_STOP=1`.
- 2.5: The reviewer opened the provisioned TimescaleDB data source in Grafana and used its `Test` button. The screenshot showed `Database Connection OK`.
- 2.6: `mosquitto_sub` subscribed to `telemetrix/m1/smoke` with `-C 1 -W 30 -v -d`. After `mosquitto_pub` sent `m1-mqtt-roundtrip-20261018`, the subscriber reported `SUBACK`, received the exact payload, and disconnected. The publisher succeeded; no retained message was used.

Group 2 is ready for review: [review package and reproduction steps](../../../docs/m1-group2-review.md). Await reviewer approval before group 3.

## 3. Document

- [x] 3.1 Fill the README "Local environment" section: start, endpoints table, UaExpert tip, reset (`down -v`); verify by following it from a fresh clone

Verification notes (2026-10-18):

- 3.1: Expanded the README's Local environment section and followed its startup, Kafka UI, Grafana, database, MQTT and reset steps from a local clone using the isolated `telemetrix-m1-doccheck` Compose project with fresh volumes. All six services ran, Kafka/TimescaleDB were healthy, SQL returned 7 tags and the measurement hypertable, Grafana reported `Database Connection OK`, and Kafka/MQTT round-trips succeeded. UaExpert instructions reflect the reviewer's G2 walkthrough; final fresh-volume manual acceptance remains task 4.1. The temporary Compose resources were removed and the original containers restarted. See the [G3 review package](../../../docs/m1-group3-review.md), including the remaining temporary-directory cleanup.

## 4. Milestone check

- [x] 4.1 Done when: values change in UaExpert, a topic can be created in Kafka UI, and `measurement` exists in TimescaleDB — tick only after 2.1, 2.2 and 2.4 pass on a fresh `down -v` / `up -d`

Acceptance passed (2026-10-18):

- After G3 review, ran `docker compose -p telemetrix -f deploy/docker-compose.yml down -v` and `up -d` against the primary local stack. Both named volumes were removed and recreated. All six services reached running, with Kafka and TimescaleDB healthy.
- Read-only queries after the reset returned 7 tags and `public | measurement` from `timescaledb_information.hypertables`.
- The reviewer confirmed completing both fresh-stack checks. The UaExpert screenshot showed a connected session with Boolean/Int32/UInt32 samples, source timestamps and Good status codes. The Kafka UI screenshot showed `{"message":"hello telemetrix"}` at partition 0, offset 0, and the reviewer confirmed deleting `scratch.test`. A subsequent broker topic listing returned no topics.
- Grafana's initial account and OPC PLC certificate were recreated with their containers. The stack remains running for subsequent work.

All 11 tasks are complete. M1 is In review pending the archive workflow and merge into main. The manual acceptance gate has passed; no repeat reset is required to close this change. The untracked `.m1-readme-check-20261018` verification clone remains outside the commits; its manual cleanup command is recorded in the G3 review package.
