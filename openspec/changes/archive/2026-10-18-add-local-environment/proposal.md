# Proposal

## Why

Every later milestone needs an OPC UA server, a Kafka broker, a database and a way to look inside them. Getting that stack reproducible first means milestone 2 onward only ever writes client code.

## What Changes

Milestone: M1 — Environment.

- One compose file (`deploy/docker-compose.yml`) starts OPC PLC, Mosquitto, Kafka (KRaft, single node), Kafka UI, TimescaleDB and Grafana (NFR-11, infrastructure part).
- TimescaleDB initialises the `asset`, `tag` and `measurement` schema and seeds the pumping line on first start.
- Kafka refuses to auto-create topics (ADR-0007).
- Secrets come from `deploy/.env`, which is git-ignored; only `.env.example` is committed (NFR-10).
- README documents how to start, inspect (UaExpert, Kafka UI, Grafana, psql) and reset the stack.

## Capabilities

### New Capabilities

- `local-environment`: the developer stack — which services exist, where they listen, how they start, and what state they start in.

### Modified Capabilities

None.

## Impact

- `deploy/` (compose, `sql/init.sql`, `grafana/`, `mosquitto/`), `README.md`.
- Requires Docker Desktop and UaExpert on the developer machine. No .NET code changes.
