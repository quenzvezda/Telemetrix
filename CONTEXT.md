# Telemetrix

Moves sensor values from a (simulated) plant to a time-series database without losing or duplicating any of them. One identity — plant / line / asset / tag — runs end to end.

## Plant hierarchy

**Plant**:
A site; the top of the identity hierarchy (`Plant01`).

**Line**:
A production line inside a plant (`Line01`).

**Asset**:
A physical machine on a line — a pump, a tank, a motor (`Pump01`). The unit of ordering: everything about one asset stays in sequence.
_Avoid_: Device, equipment, machine id

**Tag**:
One named measurement point on an asset (`MotorTemperature`), with a unit and a valid range.
_Avoid_: Signal, point, variable, node (a node is the OPC UA address of a tag, not the tag)

## Telemetry

**Sample**:
One value of one tag at one source timestamp, with its status code.
_Avoid_: Reading, data point, event

**Source timestamp**:
The time the plant says the value was measured. It is the measurement's time; server and ingest timestamps exist only to diagnose clock drift.

**Quality**:
Whether a sample can be trusted: Good, Uncertain or Bad. Derived from the OPC UA status code, and forced to Bad when the value is outside the tag's valid range.
_Avoid_: Status (that is the raw code), validity

**Batch**:
The samples of one asset collected over a short window, sent and stored as a unit and identified by a batch id.
_Avoid_: Message, packet, chunk

**Measurement**:
A sample after it has been validated, given a quality and stored.
_Avoid_: Row, record

**Rollup**:
A per-tag summary (min, max, avg, count) of Good measurements over a fixed bucket, e.g. one minute.
_Avoid_: Aggregate (ambiguous), downsample

## Edge

**Bridge**:
The edge service that collects samples from the plant and hands batches to the backbone.
_Avoid_: Gateway, collector, agent

**Source**:
A protocol the bridge collects from — OPC UA (primary) or MQTT (comparison).

**Store-and-forward buffer**:
The bridge's on-disk queue of batches that could not be sent yet, replayed in original order once the backbone is back.
_Avoid_: Cache, outbox

**Gap**:
A period with no samples for an asset because the bridge lost its source connection. Gaps are reported as plant events, never filled in.

**Plant event**:
A notice about the collection itself rather than a value: connection lost, connection restored, gap.
_Avoid_: Alarm (an alarm is about the process, not the connection)

## Backbone and IT side

**Transformer**:
The service that turns raw batches into measurements: validates, assigns quality, enriches, stores.
_Avoid_: Processor, sink (a separate sink is a stretch goal)

**Transformation**:
An operator-supplied Python script, registered under a stable label, that the transformer runs on a labeled batch after quality and range checks. It never changes raw measurements.
_Avoid_: Plugin, hook, rule

**Derived tag**:
A tag whose measurements are produced by a transformation (`TemperatureF`), with its own unit and valid range, marked with the producing label.
_Avoid_: Virtual tag, computed tag

**Disposition**:
What a transformation decided about one input sample: unchanged, modified, flagged or rejected.
_Avoid_: Verdict, status

**Dead-letter**:
A batch the transformer cannot process, set aside with the reason instead of blocking the stream.
_Avoid_: Error queue, poison message

## Consumers

**Query SDK**:
The supported Python package (`telemetrix-query`) that reads the hierarchy, tags, measurements, rollups and gaps from the database for notebooks, applications and scheduled jobs. Read-only; it never imports transformer code.
_Avoid_: Client library, driver, API (there is no query service)

**Reader role**:
The `telemetrix_reader` database role that may only read telemetry tables. Consumer logins are members of it.
_Avoid_: Read-only user, viewer
