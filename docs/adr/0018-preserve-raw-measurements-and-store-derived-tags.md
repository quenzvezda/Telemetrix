---
status: accepted
---

# Preserve raw measurements and store transformation output under derived Tags

A transformation never changes, replaces or drops a raw Measurement. The built-in pipeline (Tag resolution, Quality mapping, range check) runs first and its Measurements are stored exactly as without scripting (NFR-01, ADR-0003). The script sees only known-Tag Samples, with their raw status code, Quality, unit and range.

Script output is stored as derived Measurements in `measurement`, under derived Tags: `tag` rows marked `derived_by = <label>`, created on first use from the unit and range declared for that label in the registry. Because a derived Tag has its own `tag_id`, a derived value never collides with the raw value at the same `(tag_id, time)`, and converted values are range-checked against the derived Tag's own range and stored under its own unit. A script may only write to output Tags declared for its label; an output name that belongs to a raw Tag, to another label or with a different unit fails the run. Every output Sample must carry the source timestamp of one of its inputs, so timestamps are never invented (ADR-0009).

Each input Sample gets exactly one disposition: `unchanged`, `modified` (it fed at least one output), `flagged` (suspicious, with a reason) or `rejected` (invalid per the script, with a reason; it may not feed an output). Every output Sample is a `derived` Measurement whose Quality is the worst of its inputs' Quality, Uncertain when an input is flagged, and the script's own Quality, and is then forced to Bad when outside the derived Tag's range. Scripts can lower Quality, never raise it.

The raw Measurements, derived Measurements, flagged/rejected dispositions and one `transformation_run` row per Batch and label (status, failure reason, script version, counts, duration) are written in one database transaction before the offset is committed. Failures that will not change on retry (unknown label, script exception, timeout, crash, malformed output, Tag conflict) are recorded in that row instead of blocking the partition; raw Samples are still stored, so nothing is lost silently. The `transformation_run` primary key `(batch_id, label)` makes the first durable result final: a replay, even after a script-version change, neither runs the script again nor mixes versions.

Transformations are stateless per Batch. Windowed logic (for example an outlier score) uses only the Samples of the current Batch plus static registry parameters; there is no cross-Batch state to order, bound or recover.

## Considered Options

- **Overwrite the raw value** — conflicts with NFR-01 raw preservation, stores converted values under the wrong unit and makes the stored value depend on which script version happened to run first.
- **Separate derived table** — clean separation, but the dashboard, rollups and retention would need parallel queries and policies.
- **Publish failures to the DLQ** — re-processing belongs to controlled replay (issue #11); a database row in the same transaction is durable without coordinating a second output (ADR-0012).

## Consequences

Migration `003_transformations.sql` adds `tag.derived_by`, `transformation_run` and `sample_disposition`; it is required only when scripting is enabled. Rollups and retention apply to derived Tags like any other Tag. Lineage is per Batch: a derived Measurement's `batch_id` and Tag `derived_by` lead to the run row with the script version, and the same `batch_id` identifies its raw inputs. Re-transforming history with a new script version needs controlled replay, which is out of scope here.
