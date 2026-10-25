# M9 security review

Change: `add-opcua-security`. Implementation proceeds across all task groups with the reviewer's authorization on 2026-10-25. Archive and final milestone acceptance require PR approval and the outstanding reviewer checks.

## Configuration and startup

`OpcUa:Security` configures mode, policy, PKI root and auto-accept. Secure defaults apply in every environment; Development explicitly selects None. Startup rejects insecure settings outside Development. The old `OpcUa:SecurityMode` setting is replaced by `OpcUa:Security:Mode`.

Verification: `dotnet test --project tests/Telemetrix.Edge.Bridge.Tests -c Release` passes 110 tests, including Production/Staging/PlantSim rejection of None, Production rejection of auto-accept and acceptance of secure Production settings. Development logs a warning for insecure settings.

## Verification evidence

Fresh clone (task 4.3): `pwsh -NoProfile -File ./scripts/Test-M9FreshClone.ps1` clones the committed branch, supplies a generated test database password only through ignored `deploy/.env`, removes host port bindings with a temporary override, builds/starts the secure stack and explicitly trusts its two generated identities. Result: `M9_FRESH_CLONE_PASS: secure session; measurements=42` in isolated project `telemetrix-m9-fresh-476c95e72a36`. The harness removes only that unique project's containers, images, network and volumes plus its temporary clone. The existing local stack is preserved. Remote CI secret scanning is checked before marking task 4.3 complete.

Existing M7 volume (task 5.1, agent evidence pending reviewer acceptance): stop Bridge, verify raw consumer lag 0 on both populated partitions, then stop Transformer. Before migration: 121,100 measurements. Run the reviewed migration runner twice: after each run, 121,100 measurements remain. `measurement_1m` contains 1,834 historical rollups; jobs show one one-minute refresh policy, raw retention of 30 days and rollup retention of one year. No volume reset was performed. Transformer was subsequently started with `PLANT_EVENTS_ENABLED=true` supplied through the process environment, followed by Bridge. Set this flag in your ignored `deploy/.env` to retain it on later Compose recreations. The checkbox still requires the reviewer's observed acceptance.

Untrusted production client (task 4.2): a second production Bridge used `/tmp/rogue-pki`, `/tmp/rogue.db` and an ephemeral HTTP port, without sharing the active Bridge's identity/buffer. PlantSim repeatedly rejected its new thumbprint `B550E07916D45A33E9DC39E333975916AF6150D6` with `BadCertificateUntrusted`; the client never established a session. The trusted pipeline continued storing measurements (121,485 at source time `08:57:30 UTC`). The rogue container was removed; no trust was granted to it.

Secure production pipeline (task 4.1): the live Compose Bridge establishes Basic256Sha256 SignAndEncrypt and subscribes to all seven configured tags. After the migration pause, the measurement count rises from 121,100 to 121,317 with latest source time `2026-10-25 08:57:06+00`. This uses the production images, existing Kafka/TimescaleDB and default 500/5 s batching.

Secret scanning: Gitleaks 8.30.1 scanned all 168 reachable commits at this checkpoint with no leaks. `./scripts/Test-SecretScan.ps1 -Docker` creates an isolated `fake-private-key` branch, commits an invalid fixture and asserts scanner exit 1 (one finding). CI runs the same negative check, scans PR commits and all reachable history with redaction, and disables PR comments/artifact uploads. The only added allowlist combines the exact `deploy/.env.example` path with its exact `POSTGRES_PASSWORD=change-me` placeholder line. The action is pinned to the v3 commit because upstream retired Node 20/v2; see [upstream action documentation](https://github.com/gitleaks/gitleaks-action). Remote CI acceptance is recorded after PR creation.

Remote verification for task 3.2 passes in [PR #6's secrets job](https://github.com/quenzvezda/Telemetrix/actions/runs/40181117643/job/121132203303): the clean PR/history scans pass and the negative fixture is rejected with rule `private-key`, file `fake-key.pem`, line 1. The fixture's expected failure is converted into a passing self-test only after its exit code is asserted.

Secrets audit: `git grep -i password -- '*.json'` finds no matches. App projects already have `UserSecretsId`; no password migration was necessary. Compose supplies the database password only from ignored `deploy/.env` via environment variables. README now documents IDE user-secrets and PKI boundaries. The live secured stack starts with these providers.

Trust workflow: the README documents rejected-to-trusted movement and the thumbprint-checked `deploy/Trust-OpcUaCertificates.ps1` helper. Running it first prints identities without changes; supplying the two observed thumbprints establishes mutual trust on the live secure Compose stack. Bridge logs SignAndEncrypt/Basic256Sha256, creates seven monitored tags and reports Healthy without restarting either application. `docker inspect --format '{{.State.StartedAt}}' telemetrix-bridge-1 telemetrix-plantsim-1` retains the original startup times.

Compose verification: `docker compose -f deploy/docker-compose.yml --profile secure config --quiet` and `up -d --build` succeed on the existing stack. PlantSim logs `BadCertificateUntrusted` for the new Bridge certificate and no session is established. Mutual rejection is covered by the staged integration test: the server rejects the client before it sends its own certificate, so both rejection logs need not occur in one connection attempt. OPC PLC runs without `--ut`/`--autoaccept`, with explicit persistent store paths. M9 uses a new PlantSim secure PKI volume; the old PKI, Kafka, edge buffer and TimescaleDB volumes are preserved.

Rejected-certificate logs: secure integration verification checks that the actual untrusted-server rejection logs PlantSim's subject and exact thumbprint. Both applications log through the SDK's `AcceptError` callback and always return false; logging does not grant trust.

Certificate verification: the two `OpcUaSecurityTests` pass. Both applications generate directory-store certificates, the Bridge certificate survives configuration recreation byte-for-byte, PlantSim SAN contains `localhost` and `plantsim`, each untrusted peer is rejected, and adding trust establishes a secure session without restarting. `git check-ignore pki/own/certs/example.der` confirms the PKI directory is ignored. Test PKIs are isolated temporary directories. Existing insecure regression fixtures explicitly use Development.

The secured container dashboard check exposed a certificate-factory detail: it
normalizes the first domain entry. Listing the machine name first preserves
`localhost` in container certificates. The security integration test now binds
PlantSim to `0.0.0.0` while connecting through `localhost`, covering the actual
container case rather than only an in-process localhost listener. It passes with
hostname validation enabled. Dashboard preflight now reports certificate errors
immediately instead of waiting for the measurement deadline.

The SDK also maps loopback URLs to the host computer's real DNS name during
validation. The Docker dashboard fixture now provisions that host-facing name
while preserving the required `localhost`/`plantsim` SANs, and compares the
advertised certificate with the public certificate on disk before connecting.
Compose exposes optional `OPCUA_HOST_NAME` for the same host/NAT workflow; the
README documents setting it before certificate provisioning. Hostname checks
and mutual trust remain enabled. The M9 certificate generated earlier in this
session was backed up inside its PKI volume and explicitly replaced/re-trusted;
its former public trust entry was moved outside the trusted store. The current
PlantSim thumbprint is `182991672B1001E68494AC4122F96D44899910F2`, with both
required SANs verified, and the current production Bridge is Healthy.

Visual verification on the disposable secured dashboard shows repeated Pump01
tag trends/units, a Running/Stopped timeline and current Running state after
recovery. The gap table displays `2026-10-25 09:27:55` to `09:29:03` UTC,
duration 68.0 seconds, with broken trends across that interval. The same test
checks Stopped through Grafana's panel query within ten seconds after TripPump,
then verifies null trend markers and seven-day rollups. This is agent evidence;
the reviewer's final existing-stack acceptance remains pending.

Endpoint verification: `dotnet test --project tests/Telemetrix.Integration.Tests -c Release -- --filter-class '*OpcUaSecurityTests'` passes. OPC PLC discovery selects None exactly; a real None-only PlantSim rejects secure selection with `No matching endpoint` / `BadSecurityPolicyRejected`. OPC PLC advertises secure endpoints even with `--ut`, so the None-only negative case uses PlantSim rather than claiming OPC PLC is None-only.

Tasks 5.1 and 5.2 require the reviewer's observed results. They must remain unchecked until those results are supplied. Preserve the existing TimescaleDB volume; do not run `down -v`. See `docs/m8-review.md` for the migration and dashboard procedure.

The live secured source outage check also passes: `./scripts/Test-M9SourceGap.ps1` stops PlantSim for 60 seconds and restores it with the same persistent certificate. Both assets record a 73-second gap from `2026-10-25 09:06:33+00` to `09:07:46+00`, including reconnect time. Bridge logs a fresh SignAndEncrypt session and restores all seven monitored tags. Final visual dashboard/pump-state acceptance still belongs to the reviewer.

## Review order

1. Inspect the Bridge options/validator, exact endpoint selector and client certificate configuration. The adapter retains one certificate manager across retries and disposes it on shutdown.
2. Inspect PlantSim's explicit SAN creation, secure server policy and environment validator, then the Compose PKI volumes and thumbprint-checking trust helper.
3. Inspect `.gitleaks.toml`, the CI secrets job and isolated negative-key test. No credential-bearing appsettings files were present to migrate.
4. Inspect `OpcUaSecurityTests`, the server option tests and secured `DashboardTests`, then the existing-volume/fresh-clone evidence above.

## Reviewer acceptance: tasks 5.1 and 5.2

OpenSpec progress is 11/13 verified tasks: all implementation/security checks
are complete, including the fresh-clone check plus green remote secret scanning.
The two remaining tasks are reviewer acceptance checkpoints with agent evidence.

The agent has supplied execution evidence; these checkboxes await your observed
acceptance. OpenSpec remains active until review is approved. Never reset the
existing database volume for these checks.

For task 5.1, inspect the two SQL migrations and the recorded 121,100 -> 121,100
-> 121,100 counts above. To repeat, stop Bridge, wait for the populated raw-topic
partitions' lag to reach zero, then stop Transformer:

```powershell
docker compose -f deploy/docker-compose.yml stop bridge
docker compose -f deploy/docker-compose.yml exec -T kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server kafka:9092 --describe --group transformer
docker compose -f deploy/docker-compose.yml stop transformer
docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c 'SELECT count(*) FROM measurement'
docker compose -f deploy/docker-compose.yml --profile migrations run --rm db-migrate
```

Repeat the count and migration commands: both counts must match your new paused
baseline. Inspect `SELECT view_name FROM timescaledb_information.continuous_aggregates`
and `SELECT proc_name,hypertable_name,config FROM timescaledb_information.jobs`.
Expect `measurement_1m`, its one-minute refresh, 30-day raw retention and one-year
rollup retention, with no duplicate policies after rerunning.

Set `PLANT_EVENTS_ENABLED=true` in ignored `deploy/.env` now that migrations have
passed. Resume and enable metrics:

```powershell
docker compose -f deploy/docker-compose.yml --profile secure --profile metrics up -d --build
curl.exe -fsS http://localhost:5101/health
curl.exe -fsS http://localhost:5102/health
```

Expect Healthy for both. Existing approved certificates remain trusted. For new
identities, follow the README trust workflow; do not auto-accept them.

For task 5.2, sign into [Grafana](http://localhost:3000/d/telemetrix) using your
existing local account. The agent's separate browser reached the login page;
no existing account was accessed or changed. Select Pump01 and all tags, check
live trends/units and Running state. In UaExpert connect securely to
`opc.tcp://localhost:4840`, explicitly exchange public certificates, and call
`Objects/Plant01/Line01/Simulation/TripPump`. Expect Stopped within one ten-second
refresh, with its change/sample time. Call `StartPump` to restore Running.

```powershell
./scripts/Test-M9SourceGap.ps1
```

Expect `M9_SECURE_GAP_PASS`, two asset gaps of at least 60 seconds, fresh secured
measurements after recovery, and a break in Grafana trends across the gap.
Inspect the gap table's UTC bounds, and select Tank01 to check its two tags.
Select seven days to exercise rollups. Review `docs/load-test.md` for the
accepted NFR-06/NFR-07 evidence: D5 remains 500 samples / five seconds per asset.
Record any regression before accepting these tasks and archiving M9.

## Repeatable isolated checks

The initial Ubuntu CI run exposed host bind-mount ownership in the secured
dashboard fixture: container user `app` could not write a runner-owned host PKI
directory. The fixture now keeps PlantSim's private store inside the container
and exchanges only public DER certificates through the Docker API, preserving
the production image's existing PKI ownership and non-root execution.

The focused secured dashboard run passes. Final Release build has zero warnings
and errors. The full local regression passes **236 tests** (237 total, one
explicit long load profile excluded) in 5 minutes 57.964 seconds. Strict OpenSpec
validation passes all 13 items and `git diff --check` is clean. Remote secrets,
OpenSpec, M4 delivery and short M5 outage jobs pass; the current complete CI
status is visible on [PR #6](https://github.com/quenzvezda/Telemetrix/pull/6).
The long profile's accepted full-duration M8 evidence remains in
`docs/load-test.md`.

```powershell
dotnet build -c Release
dotnet test -c Release --no-build
pwsh -NoProfile -File ./scripts/Test-M9FreshClone.ps1
./scripts/Test-SecretScan.ps1 -Docker
docker run --rm -v "${PWD}:/repo:ro" zricethezav/gitleaks:v8.30.1 git --redact --config /repo/.gitleaks.toml --log-opts=--all /repo
npx -y @fission-ai/openspec@1.14.3 validate --all --strict
```

The fresh-clone/negative-key harnesses create and remove only their uniquely
named disposable resources. The source-gap script intentionally exercises the
existing local simulator; it does not reset volumes or change trust.
