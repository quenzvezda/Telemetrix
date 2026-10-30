---
status: accepted
---

# Select transformations by a label in Batch schema version 2

The transformation selector is an optional `transformation` field in the Batch payload, introduced as `schemaVersion` 2. The Bridge assigns it per Asset from configuration (`Transformations:Assets:<AssetId>`); because a Batch holds one Asset, two Assets can select two different scripts. A label is a stable lowercase name (`^[a-z0-9][a-z0-9-]{0,62}$`) that the Transformer resolves through its registry; a Batch never names a file or executable.

The Bridge keeps emitting `schemaVersion` 1, byte-for-byte unchanged, for Assets without a label, so an older Transformer keeps working until labels are configured. The Transformer accepts version 1 (no `transformation` field allowed) and version 2 (optional valid label). Unlabeled Batches always run only the built-in pipeline, whether scripting is enabled or not. When scripting is disabled, a labeled Batch is also processed by the built-in pipeline only; the skip is logged and counted.

With scripting enabled, a label missing from the registry records a durable `unknown-label` failure for that Batch while its raw Samples are still stored (ADR-0018). Within one deployment a label resolves to exactly one script version. A replayed Batch that already has a recorded transformation result is not transformed again, so the first durable result wins across retries and deployments.

## Considered Options

- **Kafka header** — keeps the payload unchanged, but the edge store-and-forward buffer and the DLQ persist payloads, so the selector would be lost or need parallel plumbing; it is also invisible to schema validation.
- **Transformer-side mapping by Asset** — no contract change, but the issue requires the Batch to carry the selection, and it couples the selection to Transformer deployments rather than the edge configuration.
- **Always emit version 2** — simpler Bridge code, but every older Transformer would dead-letter every Batch on upgrade.

## Consequences

`Telemetrix.Contracts` gains a nullable `Transformation` property that is omitted from JSON when null. Tools and tests that build version 1 Batches keep working. Avro/Protobuf remain a stretch goal (ADR-0008).
