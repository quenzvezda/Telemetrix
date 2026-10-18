# Design

## Context

See [proposal.md](proposal.md) for the motivation and
[local-environment/spec.md](specs/local-environment/spec.md) for the behavior
contract. This document records the configuration implemented and verified in
M1; it was completed at archive time with reviewer approval.

The development machine runs Docker Desktop with Linux containers. The .NET
services are scaffolds and are run from the host in later milestones. The
environment must therefore support both host clients and container clients.

## Goals / Non-Goals

**Goals:** reproducible startup, explicit network addresses, fail-fast secret
validation, seeded storage, and inspection with ordinary protocol clients.

**Non-Goals:** implementing Bridge or Transformer behavior, production
hardening, Kafka high availability, rollups, and retention policies. Those are
covered by later milestones and existing ADRs.

## Decisions

### Compose owns the local infrastructure lifecycle

One Compose file pins OPC PLC, Mosquitto, Kafka, Kafka UI, TimescaleDB and
Grafana images. Named volumes persist Kafka and PostgreSQL data. Kafka UI
waits for Kafka's health check; Grafana waits for PostgreSQL readiness.
This keeps startup reproducible compared with separately installed host
services, without requiring an orchestration platform for local development.

### Kafka uses separate internal and host listeners

The single-node KRaft broker follows ADR-0001. Containers bootstrap using
`kafka:9092`; host applications use `localhost:29092`. Each listener advertises
the address reachable by its clients. Using only a container hostname would
break host-side clients after metadata discovery. Topic auto-creation is off
as required by ADR-0007; later services create the topics they own.

### Database initialization runs on an empty volume

The mounted `deploy/sql/init.sql` creates asset/tag metadata, the measurement
hypertable, and the pumping-line seed. The unique `(tag_id, time)` key follows
ADR-0003; TimescaleDB follows ADR-0002. This establishes the storage boundary
before implementing the write path. Later schema changes require reviewed SQL
migrations; restarting an existing volume does not rerun initialization.

### Local secrets are external to version control

Compose reads the ignored `deploy/.env` and requires a nonempty
`POSTGRES_PASSWORD`. The committed example contains a placeholder. Grafana's
provisioned data source receives that password to connect to TimescaleDB as
`telemetry`. Grafana's own initial administrator login is separate.

### Protocol inspection precedes service implementation

OPC PLC exposes the development endpoint with security None and Anonymous
authentication. UaExpert verifies subscriptions and records candidate NodeIds
for M2. Mosquitto permits anonymous local development traffic. Kafka UI,
read-only SQL queries, and Grafana's data source test inspect the remaining
components. Host-installed brokers and a custom simulator are unnecessary for
this milestone; the project-owned simulator is scheduled for M6.

## Risks / Trade-offs

- Development listeners lack production protections → M9 addresses OPC UA
  security; the README identifies the development settings.
- OPC PLC advertises its container hostname → accept UaExpert's replacement
  with `localhost` during host discovery.
- Recreating OPC PLC can change its certificate → repeat local certificate
  acceptance and discovery when necessary.
- Grafana accounts/dashboards and OPC PLC certificates have no named volume
  in this configuration → use `stop`/`start` to retain their containers during
  normal pauses; recreation requires setup again.
- Published ports can already be occupied → inspect the startup error and
  stop the conflicting local service before retrying.
- `down -v` destroys Kafka/database data → reserve it for deliberate resets;
  test documentation on an isolated Compose project when retaining local data.

## Migration Plan

For first deployment, copy the environment example, set the password, validate
with `config --quiet`, then run `up -d` and inspect health. Verify UaExpert,
Kafka UI, database seed/hypertable, Grafana and MQTT as described in the README.

For a normal pause, use `stop`/`start`. To remove containers while preserving
the named data volumes, use `down`. For a deliberate empty-volume reset, use
`down -v` followed by `up -d`, then repeat inspection. Reset is destructive and
does not restore prior measurements or topics. No existing production data
is migrated by M1.
