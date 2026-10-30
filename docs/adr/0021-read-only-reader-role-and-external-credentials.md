---
status: accepted
---

# Read-only reader role and external credentials for consumers

Migration `004_query_reader.sql` creates a `NOLOGIN` group role `telemetrix_reader` with `CONNECT` on the database, `USAGE` on schema `public` and `SELECT` on every table and view in it, plus default privileges so tables added by later migrations are readable too. It grants nothing else: no write, no `CREATE`, no `TRUNCATE`. The migration is idempotent and applied explicitly, like the others.

Logins are created by the operator as members of that role, with the password typed interactively (`\password`) or set from a secret store, never written into a file in the repository (NFR-10). The SDK takes credentials from explicit settings or the environment — `TELEMETRIX_DATABASE_URL`, otherwise the standard libpq variables and `~/.pgpass` — and never prints or logs a password.

Independently of the role, every SDK session sets `default_transaction_read_only`, so even a login with write privileges cannot change data through the SDK. When the login can write to `measurement`, the SDK warns (`InsecureRoleWarning`) so the reader role is used by default rather than the service owner.

## Considered Options

- **Use the `telemetry` owner** — works today, but one leaked notebook credential could drop the hypertable.
- **A login role with a password in the migration** — puts a secret in source control.
- **Read-only sessions only** — a session setting can be turned off by anyone holding the credentials; the role is the actual boundary.

## Consequences

The Grafana data source and the services keep their current owner credentials; moving Grafana to the reader role is possible later. Each consumer login is managed by the operator.
