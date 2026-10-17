# AGENTS.md

Telemetrix: plant telemetry pipeline in C# / .NET 10 — OPC UA (or MQTT) → `Edge.Bridge` → Kafka → `Transformer` → TimescaleDB, watched in Grafana. Learning project; the design source of truth is `docs/plant-telemetry-pipeline.md`.

## Where things are decided

- **Vocabulary** — `CONTEXT.md`. Use its terms (Sample, Batch, Asset, Tag, Quality, Gap…) in code, logs, commits; its _Avoid_ lists are binding.
- **Why it is built this way** — `docs/adr/`. Read the relevant ADR before changing the backbone, write path, batching key, edge buffer, topic ownership, contracts or timestamps. A new hard-to-reverse choice gets a new ADR.
- **What to build next** — `ROADMAP.md`: milestones M1–M9 and open decisions D1–D5.
- **What a milestone must do** — `openspec/changes/<change>/` (proposal, specs, design, tasks). Specs of finished milestones are in `openspec/specs/`.

## Workflow

1. Work on one milestone at a time = one OpenSpec change. Check open decisions in `ROADMAP.md` first; a task that depends on an unresolved one stops and asks.
2. Implement through `/opsx:apply <change>`; tick each task in `tasks.md` only after its stated verification passes.
3. Requirement changes go into the change's delta specs (`/opsx:update`), never only into code.
4. Milestone done → `/opsx:archive <change>`, then set its status in `ROADMAP.md`.
5. Cite requirement IDs (FR-xx / NFR-xx) in test names and in commits (see Commits).

## Commands

```sh
dotnet build                                   # warnings are errors
dotnet test                                    # integration tests need Docker running
docker compose -f deploy/docker-compose.yml up -d     # needs deploy/.env (copy .env.example)
docker compose -f deploy/docker-compose.yml down -v   # reset, drops Kafka and DB data
```

## Code conventions

- Projects are `Telemetrix.<Name>`; folders inside `Edge.Bridge` (Sources, Batching, Buffer, Publishing) and `Transformer` (Consuming, Pipeline, Storage) are the module boundaries.
- Message records and their JSON options live only in `Telemetrix.Contracts`; both services serialise through it.
- Package versions live only in `Directory.Packages.props` (central package management); `PackageReference` carries no `Version`. Add a package in the milestone that first needs it.
- Shared MSBuild settings (TFM, nullable, analyzers) live in `Directory.Build.props`; project files stay minimal.
- Time is UTC `DateTimeOffset` everywhere; the measurement time is the OPC UA source timestamp (ADR-0009). Inject `TimeProvider` so batching and timeouts are testable with `FakeTimeProvider`.
- Unit tests are pure (no Kafka/Postgres); anything touching Kafka or TimescaleDB goes in `tests/Telemetrix.Integration.Tests` with Testcontainers.
- Secrets come from `deploy/.env`, environment variables or user-secrets; committed config holds placeholders only (NFR-10).

## Commits

Conventional Commits, in English, imperative mood: `<type>(<scope>): <subject>`.

- **type**: `feat`, `fix`, `test`, `refactor`, `perf`, `docs`, `build`, `ci`, `chore`, `revert`.
- **scope**: `contracts`, `bridge`, `transformer`, `plantsim`, `mqttsim`, `deploy`, `db`, `openspec`, `adr`, `docs`, `deps`. Omit only for repo-wide changes.
- **subject**: lowercase, no trailing period, ≤ 72 characters, says what the commit does.
- **body** (optional): why, and anything a reviewer cannot see in the diff.
- **footer**: `Refs: FR-13, NFR-05` for requirements, `Change: <openspec-change>` for milestone work; `BREAKING CHANGE: …` when a contract or schema changes incompatibly.

One commit = one logical step, usually one ticked task in `tasks.md`; code, its tests and its doc update travel together.

## Database Environments

PostgreSQL + TimescaleDB, not MariaDB. Access is read-only by default; schema changes go into a `.sql` file under `deploy/sql/` for review.

| Alias | Engine | Where | Database / user | Credentials |
| --- | --- | --- | --- | --- |
| `local` | TimescaleDB (PostgreSQL 17) | `localhost:5432`, compose service `timescaledb` | `telemetry` / `telemetry` | `POSTGRES_PASSWORD` in `deploy/.env` |

`deploy/sql/init.sql` runs only on an empty volume; a schema change needs a migration script plus `down -v` locally.
