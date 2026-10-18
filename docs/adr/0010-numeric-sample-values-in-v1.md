---
status: accepted
---

# Sample values are numeric in v1

D2 was accepted by the reviewer on 2026-10-18 before M2's shared `Sample` contract: values use `double?`, and source adapters convert booleans to 1/0 before emitting samples. Null can preserve a sample without a usable numeric value, including its raw status code; strings are out of scope for v1. This keeps one numeric representation through batches and `measurement` rather than introducing a separate non-numeric table or a polymorphic wire value.
