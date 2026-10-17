## ADDED Requirements

### Requirement: Single-command infrastructure startup
The local infrastructure (OPC UA simulator, MQTT broker, Kafka broker, Kafka UI, TimescaleDB, Grafana) SHALL start from a fresh clone with one `docker compose up` once `deploy/.env` exists (NFR-11).

#### Scenario: Fresh clone starts
- **WHEN** a developer copies `deploy/.env.example` to `deploy/.env` and runs `docker compose -f deploy/docker-compose.yml up -d`
- **THEN** every service reaches running (and healthy, where a health check is defined) without further manual steps

#### Scenario: Missing secret fails fast
- **WHEN** `POSTGRES_PASSWORD` is not set
- **THEN** compose refuses to start and names the missing variable

### Requirement: Well-known endpoints
The stack SHALL expose: OPC UA on `opc.tcp://localhost:50000`, MQTT on `localhost:1883`, Kafka on `localhost:29092` for host clients and `kafka:9092` for containers, Kafka UI on `http://localhost:8080`, PostgreSQL on `localhost:5432` (database `telemetry`), Grafana on `http://localhost:3000`.

#### Scenario: Host client reaches Kafka
- **WHEN** a client on the host bootstraps from `localhost:29092`
- **THEN** it receives metadata whose advertised address is reachable from the host

#### Scenario: OPC UA browsable without security
- **WHEN** UaExpert connects to `opc.tcp://localhost:50000` with security mode None
- **THEN** it can browse the address space and sees values changing

### Requirement: Seeded telemetry schema
On first start with an empty volume the database SHALL contain the `asset`, `tag` and `measurement` tables, `measurement` SHALL be a hypertable on `time` with a unique `(tag_id, time)` key, and the pumping line's 2 assets and 7 tags SHALL be seeded.

#### Scenario: Schema present after first start
- **WHEN** the stack starts on an empty `tsdata` volume
- **THEN** `SELECT count(*) FROM tag` returns 7 and `measurement` is listed in `timescaledb_information.hypertables`

### Requirement: Explicit topic creation
The Kafka broker SHALL NOT create topics implicitly; producing to a topic that does not exist MUST fail.

#### Scenario: Typo in topic name
- **WHEN** a client produces to a topic that was never created
- **THEN** the produce fails with an unknown-topic error and no topic appears in Kafka UI

### Requirement: No secrets in source control
Credentials SHALL be supplied through environment variables or user-secrets; the repository MUST contain only placeholder values (NFR-10).

#### Scenario: Env file ignored
- **WHEN** `deploy/.env` exists locally
- **THEN** `git status` does not list it
