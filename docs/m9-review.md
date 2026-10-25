# M9 security review

Change: `add-opcua-security`. Implementation proceeds across all task groups with the reviewer's authorization on 2026-10-25. Archive and final milestone acceptance require PR approval and the outstanding reviewer checks.

## Configuration and startup

`OpcUa:Security` configures mode, policy, PKI root and auto-accept. Secure defaults apply in every environment; Development explicitly selects None. Startup rejects insecure settings outside Development. The old `OpcUa:SecurityMode` setting is replaced by `OpcUa:Security:Mode`.

Verification: `dotnet test --project tests/Telemetrix.Edge.Bridge.Tests -c Release` passes 110 tests, including Production/Staging/PlantSim rejection of None, Production rejection of auto-accept and acceptance of secure Production settings. Development logs a warning for insecure settings.

## Deferred M8 acceptance

Fresh clone (task 4.3): `pwsh -NoProfile -File ./scripts/Test-M9FreshClone.ps1` clones the committed branch, supplies a generated test database password only through ignored `deploy/.env`, removes host port bindings with a temporary override, builds/starts the secure stack and explicitly trusts its two generated identities. Result: `M9_FRESH_CLONE_PASS: secure session; measurements=42` in isolated project `telemetrix-m9-fresh-476c95e72a36`. The harness removes only that unique project's containers, images, network and volumes plus its temporary clone. The existing local stack is preserved. Remote CI secret scanning is checked before marking task 4.3 complete.

Existing M7 volume (task 5.1, agent evidence pending reviewer acceptance): stop Bridge, verify raw consumer lag 0 on both populated partitions, then stop Transformer. Before migration: 121,100 measurements. Run the reviewed migration runner twice: after each run, 121,100 measurements remain. `measurement_1m` contains 1,834 historical rollups; jobs show one one-minute refresh policy, raw retention of 30 days and rollup retention of one year. No volume reset was performed. Transformer was subsequently started with `PLANT_EVENTS_ENABLED=true` supplied through the process environment, followed by Bridge. Set this flag in your ignored `deploy/.env` to retain it on later Compose recreations. The checkbox still requires the reviewer's observed acceptance.

Untrusted production client (task 4.2): a second production Bridge used `/tmp/rogue-pki`, `/tmp/rogue.db` and an ephemeral HTTP port, without sharing the active Bridge's identity/buffer. PlantSim repeatedly rejected its new thumbprint `B550E07916D45A33E9DC39E333975916AF6150D6` with `BadCertificateUntrusted`; the client never established a session. The trusted pipeline continued storing measurements (121,485 at source time `08:57:30 UTC`). The rogue container was removed; no trust was granted to it.

Secure production pipeline (task 4.1): the live Compose Bridge establishes Basic256Sha256 SignAndEncrypt and subscribes to all seven configured tags. After the migration pause, the measurement count rises from 121,100 to 121,317 with latest source time `2026-10-25 08:57:06+00`. This uses the production images, existing Kafka/TimescaleDB and default 500/5 s batching.

Secret scanning: Gitleaks 8.30.1 scanned all 168 reachable commits at this checkpoint with no leaks. `./scripts/Test-SecretScan.ps1 -Docker` creates an isolated `fake-private-key` branch, commits an invalid fixture and asserts scanner exit 1 (one finding). CI runs the same negative check, scans PR commits and all reachable history with redaction, and disables PR comments/artifact uploads. The only added allowlist combines the exact `deploy/.env.example` path with its exact `POSTGRES_PASSWORD=change-me` placeholder line. The action is pinned to the v3 commit because upstream retired Node 20/v2; see [upstream action documentation](https://github.com/gitleaks/gitleaks-action). Remote CI acceptance is recorded after PR creation.

Secrets audit: `git grep -i password -- '*.json'` finds no matches. App projects already have `UserSecretsId`; no password migration was necessary. Compose supplies the database password only from ignored `deploy/.env` via environment variables. README now documents IDE user-secrets and PKI boundaries. The live secured stack starts with these providers.

Trust workflow: the README documents rejected-to-trusted movement and the thumbprint-checked `deploy/Trust-OpcUaCertificates.ps1` helper. Running it first prints identities without changes; supplying the two observed thumbprints establishes mutual trust on the live secure Compose stack. Bridge logs SignAndEncrypt/Basic256Sha256, creates seven monitored tags and reports Healthy without restarting either application. `docker inspect --format '{{.State.StartedAt}}' telemetrix-bridge-1 telemetrix-plantsim-1` retains the original startup times.

Compose verification: `docker compose -f deploy/docker-compose.yml --profile secure config --quiet` and `up -d --build` succeed on the existing stack. PlantSim logs `BadCertificateUntrusted` for the new Bridge certificate and no session is established. Mutual rejection is covered by the staged integration test: the server rejects the client before it sends its own certificate, so both rejection logs need not occur in one connection attempt. OPC PLC runs without `--ut`/`--autoaccept`, with explicit persistent store paths. M9 uses a new PlantSim secure PKI volume; the old PKI, Kafka, edge buffer and TimescaleDB volumes are preserved.

Rejected-certificate logs: secure integration verification checks that the actual untrusted-server rejection logs PlantSim's subject and exact thumbprint. Both applications log through the SDK's `AcceptError` callback and always return false; logging does not grant trust.

Certificate verification: the two `OpcUaSecurityTests` pass. Both applications generate directory-store certificates, the Bridge certificate survives configuration recreation byte-for-byte, PlantSim SAN contains `localhost` and `plantsim`, each untrusted peer is rejected, and adding trust establishes a secure session without restarting. `git check-ignore pki/own/certs/example.der` confirms the PKI directory is ignored. Test PKIs are isolated temporary directories. Existing insecure regression fixtures explicitly use Development.

Endpoint verification: `dotnet test --project tests/Telemetrix.Integration.Tests -c Release -- --filter-class '*OpcUaSecurityTests'` passes. OPC PLC discovery selects None exactly; a real None-only PlantSim rejects secure selection with `No matching endpoint` / `BadSecurityPolicyRejected`. OPC PLC advertises secure endpoints even with `--ut`, so the None-only negative case uses PlantSim rather than claiming OPC PLC is None-only.

Tasks 5.1 and 5.2 require the reviewer's observed results. They must remain unchecked until those results are supplied. Preserve the existing TimescaleDB volume; do not run `down -v`. See `docs/m8-review.md` for the migration and dashboard procedure.
