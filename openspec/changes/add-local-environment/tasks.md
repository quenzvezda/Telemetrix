# Tasks

## 1. Bring the stack up

- [ ] 1.1 Copy `deploy/.env.example` to `deploy/.env`, set `POSTGRES_PASSWORD`, run `docker compose -f deploy/docker-compose.yml up -d`; verify with `docker compose -f deploy/docker-compose.yml ps` that every service is running and kafka/timescaledb are healthy
- [ ] 1.2 Unset `POSTGRES_PASSWORD` and run `docker compose ... config`; verify it fails naming the variable
- [ ] 1.3 Verify `git status` does not list `deploy/.env`

## 2. Inspect each component

- [ ] 2.1 Connect UaExpert to `opc.tcp://localhost:50000` (security None), browse `Objects/OpcPlc/Telemetry`; verify values change and note the NodeIds you will map to the 7 tags in M2
- [ ] 2.2 In Kafka UI (`http://localhost:8080`) create topic `scratch.test`, produce and read one message; verify, then delete the topic
- [ ] 2.3 Produce to a non-existent topic with `kafka-console-producer.sh` inside the kafka container; verify it fails and no topic is created
- [ ] 2.4 `docker compose ... exec timescaledb psql -U telemetry -d telemetry -c "SELECT count(*) FROM tag"`; verify 7, and that `measurement` appears in `timescaledb_information.hypertables`
- [ ] 2.5 Open Grafana (`http://localhost:3000`), verify the provisioned TimescaleDB data source passes "Save & test"
- [ ] 2.6 Publish and subscribe one message with `mosquitto_pub`/`mosquitto_sub` inside the mosquitto container; verify it round-trips

## 3. Document

- [ ] 3.1 Fill the README "Local environment" section: start, endpoints table, UaExpert tip, reset (`down -v`); verify by following it from a fresh clone

## 4. Milestone check

- [ ] 4.1 Done when: values change in UaExpert, a topic can be created in Kafka UI, and `measurement` exists in TimescaleDB — tick only after 2.1, 2.2 and 2.4 pass on a fresh `down -v` / `up -d`
