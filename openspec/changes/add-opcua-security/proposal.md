# Proposal

## Why

Until now the bridge talks to the OPC UA server with SecurityMode None and any client is accepted — fine for learning, unacceptable on a real plant network. This change adds certificate-based Sign & Encrypt with explicit trust, and proves that no secret lives in the repository.

Milestone: M9 — Security.

## What Changes

- NFR-09: the bridge and PlantSim support Basic256Sha256 Sign & Encrypt with application certificates and trusted-certificate lists; untrusted certificates are rejected in both directions.
- NFR-09: security mode and policy are configuration; SecurityMode None is allowed only when the environment is Development.
- NFR-10: no credentials or private keys in source control; secrets come from environment variables or user-secrets, and CI scans the repository for secrets.

## Capabilities

### New Capabilities
- `connection-security`: how pipeline components authenticate and protect their connections, and how secrets are supplied.

### Modified Capabilities
<!-- none -->

## Impact

- Code: OPC UA source in `Telemetrix.Edge.Bridge/Sources`, server configuration in `Telemetrix.PlantSim`, startup validation in both.
- Configuration: new `OpcUa:Security` section; certificate store paths (under git-ignored `pki/`).
- Infrastructure: certificate volumes in `deploy/docker-compose.yml`; OPC PLC runs with security enabled in a secure profile.
- CI: a secret-scanning step in `.github/workflows/ci.yml`.
- Docs: certificate trust workflow in the README.
