# Design

## Context

The bridge uses the OPC Foundation .NET client with SecurityMode None and auto-accept; OPC PLC runs with `--autoaccept --ut`; PlantSim (M6) is built on the OPC Foundation server SDK. `.gitignore` already excludes `**/pki/`, `*.db` and `.env`, and `deploy/.env.example` documents `POSTGRES_PASSWORD`. Secrets elsewhere (Kafka has none locally; Postgres connection string) are currently passed via configuration.

## Goals / Non-Goals

**Goals:**
- Basic256Sha256 SignAndEncrypt between bridge and both simulators, with explicit trust on both sides.
- Fail-closed configuration: insecure mode is a deliberate development choice, never a fallback.
- A CI guard for NFR-10.

**Non-Goals:**
- OPC UA user authentication (username/password or X.509 user tokens) — anonymous user identity over a secure channel is enough for this milestone.
- TLS/SASL for Kafka, Postgres or Mosquitto.
- A certificate authority / GDS; self-signed application certificates with manual trust are the learning goal.

## Decisions

### Certificate stores
The OPC Foundation `ApplicationConfiguration` uses directory stores under a configurable root (default `./pki`): `own/`, `trusted/`, `issuer/`, `rejected/`. Each application creates its self-signed instance certificate on first start (`CheckApplicationInstanceCertificates`). `pki/` is git-ignored; in compose each service mounts a named volume at its pki root so certificates survive container recreation.
`AutoAcceptUntrustedCertificates = false` everywhere outside Development. Rejected certificates land in `rejected/`; trusting one is moving the file into `trusted/` (the stack re-reads directory stores on each validation, so no restart).

### Trust workflow
- **PlantSim ↔ bridge**: start both once; each rejects the other and writes the peer's certificate to `rejected/`. Move the bridge cert into PlantSim's `trusted/` and PlantSim's cert into the bridge's `trusted/` (documented `docker compose cp` commands, or a `deploy/scripts/trust-certs.sh` helper).
- **OPC PLC**: drop `--ut`/`--autoaccept` in a `secure` compose profile and mount its pki volume; trust works the same way (its stores are under `/app/pki`).
- The bridge logs the subject and thumbprint of every rejected server certificate so trust decisions are informed.

### Configuration
```json
"OpcUa": {
  "Security": {
    "Mode": "SignAndEncrypt",          // None | Sign | SignAndEncrypt
    "Policy": "Basic256Sha256",
    "PkiRoot": "pki",
    "AutoAcceptUntrusted": false
  }
}
```
Endpoint selection filters `GetEndpoints` results by exact mode and policy URI and fails if none match — never `CoreClientUtils.SelectEndpoint(useSecurity: false)` fallback. An options validator (`IValidateOptions`) with `ValidateOnStart` enforces: `Mode=None` or `AutoAcceptUntrusted=true` only when `IHostEnvironment.IsDevelopment()`.
Alternative: always secure, including dev — rejected; NFR-09 explicitly keeps None for early development and OPC PLC's default.

### Secrets (NFR-10)
- Postgres password: `deploy/.env` (git-ignored) for compose; `dotnet user-secrets` for services run from the IDE (`ConnectionStrings:Telemetry`); environment variables in containers. `appsettings*.json` contain no passwords.
- Certificate private keys stay in `pki/own/private` volumes, never in the repo.
- CI: add a `gitleaks/gitleaks-action` step (full history on PRs) to `.github/workflows/ci.yml`; a `.gitleaks.toml` allowlists `deploy/.env.example` placeholder values only.
Alternative: GitHub secret scanning only — rejected; it is not available on every plan/fork and does not run locally. Gitleaks also runs locally via `gitleaks detect`.

## Risks / Trade-offs

- [Clock skew makes fresh certificates "not yet valid"] → certificates are created with a small back-dated validity by the SDK; document checking container clocks if validation fails.
- [Hostname mismatch in the server certificate when reached as `localhost` vs `plantsim`] → include both DNS names in PlantSim's certificate SAN; for OPC PLC set `--ph` to the compose service name and connect via that name from the bridge container.
- [Someone sets AutoAcceptUntrusted in production config] → the startup validator refuses it outside Development.
- [Gitleaks false positives on test fixtures] → narrow allowlist by path, not by rule.

## Migration Plan

Development defaults stay `Mode=None` so M1–M8 workflows keep working; `appsettings.Production.json` and the `secure` compose profile switch to SignAndEncrypt. Rollback is a configuration change.
