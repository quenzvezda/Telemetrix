## ADDED Requirements

### Requirement: Installable Python query package
The repository SHALL provide a Python package `telemetrix-query` (import `telemetrix_query`) that a separate consumer installs with pip from `sdk/python/` and uses to query stored telemetry without importing Transformer code or writing SQL. The supported language scope SHALL be Python; a .NET package SHALL be recorded as deferred (FR-50, ADR-0019).

#### Scenario: Clean consumer installation
- **WHEN** the package is built as a wheel and installed into a new virtual environment
- **THEN** a script in that environment imports `telemetrix_query` and queries Tags and Measurements from TimescaleDB

### Requirement: Hierarchy and Tag metadata
The SDK SHALL list Plants, Lines, Assets and Tags, filtered by Plant, Line, Asset, Asset type, Tag names and raw/derived, returning each Tag with its id, hierarchy, unit, valid range and the producing transformation label for derived Tags (FR-50).

#### Scenario: Tags of one Asset
- **WHEN** a consumer lists the Tags of `Pump01`
- **THEN** the five Pump01 Tags are returned ordered by name with unit and valid range, and `derived_by` is empty for each

#### Scenario: Derived Tags only
- **WHEN** migration 003 is applied, a derived Tag exists and a consumer lists Tags with `derived=True`
- **THEN** only derived Tags are returned, each naming its transformation label

### Requirement: Measurement queries by hierarchy, Tag, interval and Quality
The SDK SHALL return Measurements whose source timestamp lies in a half-open interval `start <= time < end`, filtered by Plant, Line, Asset, Tag names or Tag ids and Quality, ordered by time and Tag id, each with UTC time, Tag identity, unit, value, Quality, Batch id and ingest time (FR-51, ADR-0009, ADR-0020).

#### Scenario: Interval boundaries
- **WHEN** Measurements exist exactly at `start` and exactly at `end`
- **THEN** the one at `start` is returned and the one at `end` is not

#### Scenario: Quality filter
- **WHEN** a consumer asks for Bad Measurements of one Asset in an interval
- **THEN** only Measurements with Quality Bad are returned

### Requirement: Explicit time zones and UTC results
Interval arguments SHALL be timezone-aware datetimes or ISO-8601 strings with an offset and SHALL be converted to UTC; naive values and `end <= start` SHALL be rejected before any query runs. Returned timestamps SHALL be timezone-aware UTC (FR-51, ADR-0020).

#### Scenario: Offset input
- **WHEN** the same instant is passed once as UTC and once with a +07:00 offset
- **THEN** both queries return the same Measurements

#### Scenario: Naive datetime
- **WHEN** a consumer passes a datetime without a time zone
- **THEN** a `ValueError` explains that an aware datetime is required and no query runs

### Requirement: Null values, Bad Quality and empty results are distinct
A stored null value SHALL be returned as `None` with its stored Quality; Bad Measurements SHALL be returned unless filtered out by the caller; an interval without Measurements SHALL return an empty page without error (FR-51).

#### Scenario: Null, Bad and absent
- **WHEN** an interval holds one Good value, one Bad value outside the range and one null Bad value, and an adjacent interval holds nothing
- **THEN** the first query returns three Measurements with values number, number and `None` and Qualities Good, Bad and Bad, and the second returns an empty page

### Requirement: Bounded pages and streaming
Measurement, Rollup and Gap queries SHALL return at most `limit` items (default 10,000, maximum 100,000) and an opaque cursor that continues after the last key; iterators SHALL stream all matching items one page at a time. A cursor SHALL be rejected for a different query (FR-51, ADR-0020).

#### Scenario: Large result streamed
- **WHEN** 25,000 Measurements match and a consumer iterates with a page size of 1,000
- **THEN** all 25,000 are returned once each, in order, while at most one page is held

#### Scenario: Concurrent inserts do not shift pages
- **WHEN** a Measurement with a later time is stored between reading page one and page two
- **THEN** page two continues exactly after page one's last Measurement

#### Scenario: Cursor reused for another query
- **WHEN** a cursor from a Pump01 query is passed to a Tank01 query
- **THEN** `InvalidCursorError` is raised

### Requirement: One-minute Rollups
The SDK SHALL return the one-minute Rollups (min, max, avg, count of Good Measurements) whose bucket start lies in `start <= bucket < end`, filtered like Measurements, and SHALL raise `FeatureUnavailableError` naming migration 001 when the Rollups do not exist (FR-31, FR-51).

#### Scenario: Rollup of Good data
- **WHEN** a Tag has Good values 10, 20, 30 and a Bad value 999 in one minute and the aggregate has been refreshed
- **THEN** its Rollup for that minute has min 10, max 30, avg 20 and count 3

#### Scenario: Migration missing
- **WHEN** a consumer requests Rollups from a database without migration 001
- **THEN** `FeatureUnavailableError` names `001_rollups_and_retention.sql`

### Requirement: Collection Gaps
The SDK SHALL return Gap plant events whose span overlaps the interval, filtered by hierarchy, ordered by Gap start, and SHALL raise `FeatureUnavailableError` naming migration 002 when plant events are not stored (FR-40, FR-51).

#### Scenario: Overlapping Gap
- **WHEN** a Gap spans 10:00–10:05 and a consumer queries 10:04–11:00
- **THEN** the Gap is returned with its UTC start, end and reason

### Requirement: Timeouts, cancellation and actionable errors
Every query SHALL run with a statement timeout (default 30 seconds, overridable per client and per call) and SHALL be cancellable from another thread. Timeouts, cancellations, connection failures, missing privileges and schema mismatches SHALL raise distinct SDK exception types whose messages say what to do and never contain a password (FR-51).

#### Scenario: Timeout
- **WHEN** a query is blocked longer than its timeout
- **THEN** `QueryTimeoutError` is raised and the client remains usable

#### Scenario: Missing privilege
- **WHEN** a login without `telemetrix_reader` membership queries Measurements
- **THEN** `PermissionDeniedError` names the `telemetrix_reader` role

#### Scenario: Unreachable database
- **WHEN** the configured host refuses connections
- **THEN** `ConnectionFailedError` is raised and its message does not contain the password

### Requirement: Read-only access and external credentials
Migration `004_query_reader.sql` SHALL create a `telemetrix_reader` role with SELECT on all tables and views in schema `public`, including tables added later, and no other privilege; it SHALL be idempotent. SDK sessions SHALL be read-only regardless of the login. Credentials SHALL come from explicit settings, `TELEMETRIX_DATABASE_URL` or libpq environment variables, never from committed files, and the SDK SHALL warn when the login can write Measurements (FR-52, NFR-10, ADR-0021).

#### Scenario: Reader cannot write
- **WHEN** a login in `telemetrix_reader` tries to insert, update, delete, truncate or create a table
- **THEN** each statement fails with insufficient privilege, while SELECT on Measurements, Rollups and plant events succeeds

#### Scenario: Migration rerun
- **WHEN** migration 004 runs twice on a database with migrations 001–003
- **THEN** the second run succeeds and the grants are unchanged

#### Scenario: Owner login through the SDK
- **WHEN** the SDK connects as the database owner
- **THEN** `InsecureRoleWarning` is issued and a write attempted on the session fails as read-only

### Requirement: Schema compatibility
The SDK SHALL check at connect that `asset`, `tag` and `measurement` exist and SHALL detect Rollups, Gaps and derived Tags, exposing them as capabilities. Each release SHALL document the schema it supports and follow SemVer with a 0.x major (FR-50, ADR-0019).

#### Scenario: Initial schema only
- **WHEN** the SDK connects to a database with only `init.sql` applied
- **THEN** Tag and Measurement queries work, `derived_by` is empty and the capabilities report no Rollups, Gaps or derived Tags

#### Scenario: Not a Telemetrix database
- **WHEN** the SDK connects to a database without the `measurement` table
- **THEN** `SchemaMismatchError` is raised at connect
