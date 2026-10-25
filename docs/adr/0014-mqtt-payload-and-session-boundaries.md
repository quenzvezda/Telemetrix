---
status: accepted
---

# Keep MQTT behind the shared source adapter

M7 follows ADR-0006: MQTT 3.1.1 is a comparison source behind `ISourceAdapter`.
The reviewer accepted a separate MqttSim process with a shared pumping-line model
on 2026-10-25 and authorized autonomous implementation of all M7 task groups.

Use one QoS 1, retained publication per tag on
`plant/{plant}/{line}/{asset}/{tag}`. A shared JSON payload carries a finite numeric,
Boolean or null value, UTC source timestamp and `good`, `uncertain` or `bad`
quality. The Bridge converts Boolean values to 1/0 (ADR-0010), derives identity
from the topic, and maps quality to the OPC UA severity bits. Unknown quality
becomes Bad. Malformed payloads are rejected and counted without stopping collection.
Source timestamps remain measurement time (ADR-0009); receive time supplies the
diagnostic server timestamp. Existing numeric batch contracts and Transformer stay unchanged.

For the protocol comparison, request OPC UA `StatusValueTimestamp` notifications.
A constant Boolean still has fresh source measurements; relying on the default
status/value trigger would suppress them. The Bridge forwards real source-time
updates and does not manufacture samples or fill collection gaps.

Use a stable, unique client id and a persistent broker session for ordinary
disconnects, with acknowledgment only after bounded channel admission. QoS 1 can
duplicate samples; the existing `(tag_id, time)` uniqueness removes duplicates.
This does not add durability to samples still in memory or promise infinite
broker retention. The development Mosquitto config does not persist sessions
across broker process restarts.

At buffer capacity, deliberately disconnect collection (ADR-0013). When capacity
returns, reset the broker session once before subscribing persistently again;
history queued during the intentional collection pause is outside the acceptance
boundary. Retained last values preserve their original source times. Preserve
accepted batches and publish durable, ordered `buffer-capacity` transitions and
a gap from the last collected source timestamp to the first newer timestamp.
Normal reconnects preserve queued deliveries instead of resetting sessions.

Rejected: independent simulator models (drift); JSON booleans in batches (breaks
ADR-0010); QoS 2 (adds hop cost without changing end-to-end idempotency); replaying
intentional capacity-pause history as if it had been collected.
