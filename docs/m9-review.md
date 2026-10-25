# M9 security review

Change: `add-opcua-security`. Implementation proceeds across all task groups with the reviewer's authorization on 2026-10-25. Archive and final milestone acceptance require PR approval and the outstanding reviewer checks.

## Configuration and startup

`OpcUa:Security` configures mode, policy, PKI root and auto-accept. Secure defaults apply in every environment; Development explicitly selects None. Startup rejects insecure settings outside Development. The old `OpcUa:SecurityMode` setting is replaced by `OpcUa:Security:Mode`.

Verification: `dotnet test --project tests/Telemetrix.Edge.Bridge.Tests -c Release` passes 110 tests, including Production/Staging/PlantSim rejection of None, Production rejection of auto-accept and acceptance of secure Production settings. Development logs a warning for insecure settings.

## Deferred M8 acceptance

Tasks 5.1 and 5.2 require the reviewer's observed results. They must remain unchecked until those results are supplied. Preserve the existing TimescaleDB volume; do not run `down -v`. See `docs/m8-review.md` for the migration and dashboard procedure.
