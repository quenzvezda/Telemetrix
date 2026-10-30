---
status: accepted
---

# Stable query API with half-open UTC intervals and keyset pages

The SDK exposes typed query methods only — hierarchy, Tags, Measurements, Rollups and Gaps — and no SQL escape hatch. Every statement it runs is written in the SDK and parameterised; user input is never spliced into SQL. Ad-hoc SQL remains possible with psycopg and the same reader role, outside the supported API, so the API can stay small and its results stable.

**Time.** Interval arguments are timezone-aware datetimes (or ISO-8601 strings with an offset); naive values are rejected, because guessing a zone silently shifts data. Intervals are half-open, `start <= time < end`, on the source timestamp (ADR-0009), so adjacent intervals never return a Measurement twice. Rollups are selected by bucket start in the same interval; Gaps are returned when they overlap it. Returned times are UTC.

**Ordering and pages.** Measurements and Rollups are ordered by `(time, tag_id)`, which is unique (ADR-0003); Gaps by `(gap_start, event_id)`. A page holds at most `limit` items (default 10,000, maximum 100,000) and an opaque cursor to the next one; the cursor is a keyset position, not an offset, so pages never repeat or skip rows that existed when the first page was read, even while new rows arrive. A cursor is bound to its query and rejected for a different one. Streaming iterators read page by page, so memory stays bounded by one page. Measurements stored later with a time before the cursor (a late or replayed Batch) appear only in a new query.

**Values.** No Measurements is an empty page. A stored null value is `None`, distinct from Quality; Bad Quality is reported, never filtered out unless the caller asks. Rollups contain Good Measurements only, as the database defines them (FR-31), and only buckets the continuous aggregate has materialised.

**Errors and limits.** Each call runs in its own read-only transaction with a statement timeout (default 30 s, per-call override). A timeout, a cancellation, a missing privilege, a lost connection and a missing migration each raise their own exception type with an actionable message and no credentials.

## Considered Options

- **Offset pagination** — simple, but pages shift while Measurements arrive.
- **Server-side cursors** — true streaming, but hold a transaction and snapshot open for as long as a notebook iterates.
- **Raw SQL method** — convenient, but every caller would depend on table layout, which is exactly what the SDK exists to hide.

## Consequences

Callers that need a query the API lacks either ask for an API addition or use psycopg directly, accepting schema coupling. A .NET SDK (ADR-0019) must follow the same semantics.
