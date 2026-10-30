---
status: accepted
---

# Ship a Python query SDK first; defer .NET

Consumers that read stored telemetry get one supported package instead of copying SQL and schema knowledge: the Python distribution `telemetrix-query` (import `telemetrix_query`), kept in `sdk/python/` and installed locally with `pip install ./sdk/python`. It talks to TimescaleDB directly through psycopg 3 (ADR-0002); there is no query service in between. Public publication to PyPI is a separate step.

Python ships first because the consumers named in issue #8 are notebooks, scheduled jobs and the proposed Prefect integration (issue #9), all Python. A .NET package (`Telemetrix.Query`) is **deferred**, not rejected: .NET code that needs telemetry today lives inside this repository, and a second SDK would double the API surface to keep in step with the schema before the first one has users. It can be added later against the same reader role and query semantics (ADR-0020).

The SDK never imports Transformer code and is not used by the services. It reads `asset`, `tag`, `measurement` and, when their migrations are applied, `measurement_1m` (001), `plant_event` (002) and `tag.derived_by` (003). At connect it checks that the core tables exist and detects the optional ones, so a missing migration gives an actionable error naming the file instead of a SQL error.

Versioning follows SemVer with a 0.x major while the schema can still change: a minor bump may change the API, a patch never does. Each release documents the schema it supports (initial schema plus migrations 001–004). A schema change that renames or removes a column the SDK reads is a breaking change for the SDK and lands with an SDK release in the same change.

## Considered Options

- **.NET first** — fits the repository's tooling, but none of the requested consumers is .NET, and Prefect needs Python.
- **Both at once** — two packages, two test suites and two compatibility policies before any consumer exists.
- **A query HTTP service** — adds a deployed component, authentication and an API version of its own; direct read-only database access is what issue #8 asks for.

## Consequences

The repository gains a Python project with its own tests and CI job. Python 3.11 or newer and network access to PostgreSQL are required on the consumer side. Schema changes now have an external reader to keep compatible.
