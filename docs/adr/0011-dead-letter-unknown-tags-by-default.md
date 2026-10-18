---
status: accepted
---

# Dead-letter unknown tags by default

D1 was accepted by the reviewer on 2026-10-18 before M3: the transformer defaults to the `dead-letter` policy for unknown tags, while `create` is an explicit configuration opt-in (FR-23). Unknown-tag samples go to `plant.telemetry.dlq` with the original batch id and reason `unknown-tag`; known-tag samples in the same batch are still stored. This exposes naming or configuration mistakes without silently creating metadata; the `create` policy remains available when automatic tag creation without unit or valid range is intentional.
