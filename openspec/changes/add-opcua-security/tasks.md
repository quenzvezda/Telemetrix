# Tasks

On 2026-10-25 the reviewer approved M8 PR #5 and its archival, deferring manual testing until M9 is complete. Group 5 carries those unverified M8 checkpoints forward; complete it before final M9 acceptance. M9 implementation has not started.

## 1. Configuration and fail-closed startup

- [x] 1.1 Add the `OpcUa:Security` options (mode, policy, PKI root, auto-accept) with an `IValidateOptions` + `ValidateOnStart` rule that rejects None/auto-accept outside Development; verify unit tests cover Production+None (fails), Development+None (starts with warning), Production+SignAndEncrypt (passes)
- [ ] 1.2 Select the endpoint by exact security mode and policy with no fallback; verify a unit/integration test against an OPC PLC container exposing only None fails with the "no matching endpoint" message

## 2. Certificates and trust

- [ ] 2.1 Configure directory certificate stores under the PKI root for the bridge and PlantSim, creating self-signed instance certificates on first start (PlantSim SAN includes `localhost` and `plantsim`); verify `pki/own/certs` contains a certificate after first start and `git status` shows nothing under `pki/`
- [ ] 2.2 Log subject and thumbprint of every rejected peer certificate; verify the log line appears when the bridge first connects to an untrusted PlantSim
- [ ] 2.3 Add pki volumes and a `secure` compose profile (PlantSim and OPC PLC without `--ut`/`--autoaccept`); verify `docker compose --profile secure up` starts and the first connection is rejected on both sides
- [ ] 2.4 Write the trust workflow (move from `rejected/` to `trusted/`, optional helper script) in the README; verify following it establishes a SignAndEncrypt session without restarting either service

## 3. Secrets

- [ ] 3.1 Move every password out of `appsettings*.json` into user-secrets (IDE) and environment variables (containers); verify `git grep -i password -- '*.json'` returns no value-bearing match and the stack still starts
- [ ] 3.2 Add a gitleaks step and `.gitleaks.toml` (allowlisting only `deploy/.env.example`) to `.github/workflows/ci.yml`; verify CI passes on the clean repo and fails on a throwaway branch that commits a fake private key

## 4. Milestone verification

- [ ] 4.1 With the `secure` profile, connect the bridge to PlantSim with Basic256Sha256 SignAndEncrypt; verify the session's security mode in the bridge log and rows still arriving in `measurement`
- [ ] 4.2 Connect with a client whose certificate is not trusted (e.g. a second bridge instance with a fresh PKI root or UaExpert); verify it is rejected (M9 "Done when", NFR-09)
- [ ] 4.3 Run the fresh-clone check: clone, supply secrets only via `deploy/.env` and user-secrets, `docker compose up`; verify the stack runs and the CI secret scan is green (NFR-10)

## 5. Deferred M8 manual verification before M9 acceptance

- [ ] 5.1 Complete archived M8 task 4.2 on the existing M7 volume: review SQL, stop input, drain Transformer, record the measurement count, run the explicit migration command, and verify unchanged count, rollups and 30-day/raw plus one-year/rollup policies; repeat the migration safely. Follow `docs/m8-review.md`; record the reviewer's observed results without resetting the existing volume.
- [ ] 5.2 Complete archived M8 task 6.5 after M9 implementation: have the reviewer verify live tag trends, pump state within one refresh, and a 60-second source outage with gap bounds and broken trends on the final secured stack; confirm D5 remains 500 samples / 5 seconds, review the recorded NFR-06/NFR-07 evidence and obtain final manual acceptance. Record any regression and its resolution before M9 acceptance.
