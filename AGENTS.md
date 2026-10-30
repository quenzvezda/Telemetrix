# AGENTS.md

Telemetrix: plant telemetry pipeline in C# / .NET 10 — OPC UA (or MQTT) → `Edge.Bridge` → Kafka → `Transformer` → TimescaleDB, watched in Grafana. Learning project; the design source of truth is `docs/plant-telemetry-pipeline.md`.

## Where things are decided

- **Vocabulary** — `CONTEXT.md`. Use its terms (Sample, Batch, Asset, Tag, Quality, Gap…) in code, logs, commits; its _Avoid_ lists are binding.
- **Why it is built this way** — `docs/adr/`. Read the relevant ADR before changing the backbone, write path, batching key, edge buffer, topic ownership, contracts or timestamps. A new hard-to-reverse choice gets a new ADR.
- **What to build next** — `ROADMAP.md`: milestones M1–M9 and open decisions D1–D5.
- **What a milestone must do** — `openspec/changes/<change>/` (proposal, specs, design, tasks). Specs of finished milestones are in `openspec/specs/`.

## Roles

The user is the **reviewer and decision-maker**; the agent writes all code, tests and docs. The user reviews diffs, tests end to end by hand (Docker, Kafka UI, database, Grafana) and decides open questions. Talk to the user in Indonesian; everything written to the repo (code, docs, specs, ADRs, commits) is English.

## Session start

The user will not remember where the project stands. On the first message of a session (a greeting, "lanjut", "status", a question about progress), orient before acting:

1. Read `ROADMAP.md` (milestone status, open decisions), run `npx -y @fission-ai/openspec@1.14.3 list` (task progress per change), `git log --oneline -10` and `git status`.
2. Report in a few lines: current milestone, tasks done/remaining, anything uncommitted or awaiting review, and the decisions blocking the next step.
3. Propose the next step and wait for the user's go.

## Milestone loop

1. **Start** — branch `m<N>-<change>` from `main`; set the milestone to *In progress* in `ROADMAP.md`. If it needs an open decision, discuss it with the user first, then record an ADR.
2. **Build** — `/opsx:apply <change>`: implement `tasks.md` in order, run each task's verification, tick the box only when it passes, one commit per task. A task that depends on an unresolved decision stops and asks.
3. **Review checkpoint** — after each task group, stop and hand the user a **review package**: what changed and why (commits), what to read in the diff, and step-by-step manual checks — exact commands, URLs (Kafka UI, Grafana), SQL to run, and the expected result. Wait for approval or feedback before the next group.
4. **Change of plan** — requirement changes go into the change's specs via `/opsx:update`, never only into code.
5. **Finish** — when the milestone's "Done when" passes the user's manual check: `/opsx:archive <change>`, set *Done* in `ROADMAP.md`, merge the branch into `main`.

Progress lives in `tasks.md` checkboxes (task level), `ROADMAP.md` status (milestone level) and git history — keep all three current so the next session can orient from them alone.

## Commands

```sh
dotnet build                                   # warnings are errors
dotnet test                                    # integration tests need Docker running
docker compose -f deploy/docker-compose.yml up -d     # needs deploy/.env (copy .env.example)
docker compose -f deploy/docker-compose.yml down -v   # reset, drops Kafka and DB data
```

## Code conventions

- Projects are `Telemetrix.<Name>`; folders inside `Edge.Bridge` (Sources, Batching, Buffer, Publishing) and `Transformer` (Consuming, Pipeline, Scripting, Storage) are the module boundaries.
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
