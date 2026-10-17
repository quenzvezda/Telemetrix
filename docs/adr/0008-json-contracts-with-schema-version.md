---
status: accepted
---

# Message contracts are JSON records in a shared project, versioned by `schemaVersion`

`Telemetrix.Contracts` holds the batch, sample and plant-event records plus their `JsonSerializerOptions`; the bridge and transformer both reference it so they cannot disagree on the shape. Payloads are JSON for readability and carry `schemaVersion`, which the transformer uses to reject or upgrade old formats. Avro/Protobuf with a schema registry is a deliberate stretch goal, not v1.
