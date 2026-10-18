# M1 group 3 review

Historical review checkpoint. M1 was subsequently accepted and [archived](../openspec/changes/archive/2026-10-18-add-local-environment/tasks.md) on 2026-10-18.

Change: `add-local-environment`. Task: 3.1. Date: 2026-10-18.

## What changed

The README's **Local environment** section now documents first startup in
PowerShell, all six endpoints, the separate Grafana and PostgreSQL accounts,
UaExpert discovery and certificate prompts, Kafka inspection, read-only
database checks, MQTT, and stop/resume/reset behavior.

The stop guidance distinguishes named Kafka/database volumes from Grafana and
OPC PLC container state. This helps a developer retain local Grafana accounts
and the OPC PLC certificate during an ordinary pause.

## Verification

- Created a local Git clone of `m1-add-local-environment` under
  `.m1-readme-check-20261018` and copied the candidate README into it.
- Confirmed that the clone initially had no `deploy/.env`, copied the example,
  and set a generated password used only for this check, without printing it.
- Set `COMPOSE_PROJECT_NAME=telemetrix-m1-doccheck` for the verification
  processes to isolate new volumes from the existing `telemetrix` project.
  Stopped the original containers temporarily to free the documented ports.
- Followed the README's `config --quiet`, `up -d`, and `ps -a` commands. All six
  services ran, with Kafka and TimescaleDB healthy.
- The documented SQL returned `7` tags and `public | measurement`.
- Followed the browser steps to log in to the fresh Grafana instance using
  its initial account, open the provisioned TimescaleDB data source and click
  **Test**. Result: **Database Connection OK**. The temporary login's password
  change was skipped for this disposable instance; no new Grafana password
  was set by the agent.
- Followed Kafka UI to create `scratch.test` with one partition and replication
  factor one, produce and read `{"message":"hello telemetrix"}`, and delete the
  topic. The UI reported successful deletion and no remaining topics.
- Followed the MQTT commands; the subscriber received
  `telemetrix/m1/smoke hello` and exited successfully.
- Followed `down -v` / `up -d` only for `telemetrix-m1-doccheck`. Fresh volumes
  again produced six running services, healthy Kafka/TimescaleDB, 7 tags and
  the measurement hypertable.
- UaExpert instructions incorporate the reviewer's earlier successful manual
  walkthrough and observed node inventory. A fresh UaExpert session was not
  repeated against the disposable project. The final fresh-volume manual
  milestone check remains task 4.1.
- Deleted the temporary Compose project's containers, network and volumes.
  Started the original containers again and confirmed all six running, with
  Kafka and TimescaleDB healthy. The original volumes and container identities
  were retained.
- The attempt to delete the temporary clone directory was blocked by the
  local lean-ctx shell allowlist. The directory remains untracked and is not
  included in the task commit. Its `.env` is ignored by Git.
- `git diff --check` passed. No .NET code changed, so build/test runs were not
  needed for this documentation task.

## Review steps

Read [README.md, Local environment](../README.md#local-environment), especially
the account table, UaExpert prompts and reset explanation.

Run these commands from the primary repository root:

```powershell
git show --stat c82e289
git show c82e289 -- README.md openspec/changes/add-local-environment/tasks.md docs/m1-group3-review.md
docker compose -f deploy/docker-compose.yml ps -a
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c "SELECT count(*) FROM tag"
```

Expected: the commit changes documentation and task progress; six services are
running, Kafka and TimescaleDB are healthy, and the query returns `7`.

Open [Kafka UI](http://localhost:8080) and [Grafana](http://localhost:3000) to
confirm that the primary stack is accessible again. Use your existing Grafana
password. UaExpert can reconnect to `opc.tcp://localhost:50000`.

The temporary clone can be removed manually from the primary repository root:

```powershell
Remove-Item -LiteralPath .m1-readme-check-20261018 -Recurse -Force
```

This deletes only the disposable verification clone. Its Compose resources
have already been removed.

## Checkpoint

Task 3.1 is complete. M1 remains **In progress** with 10 of 11 tasks complete.
Await reviewer approval before task 4.1, which requires a fresh-volume manual
check of UaExpert, Kafka UI and TimescaleDB. No archive or merge is performed
at this checkpoint.
