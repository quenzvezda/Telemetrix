# Design

## Context

M2 builds only the input side of the bridge; output is the log. M3 replaces the log with the batcher. OPC PLC is the server until M6, so its generic nodes are mapped to the pumping line's tag names in configuration.

## Goals / Non-Goals

**Goals:** a source adapter M3 can plug straight into the batcher; a `Sample` contract the transformer will share.

**Non-Goals:** batching and Kafka (M3), backoff tuning and connection events (M5), any security mode other than None (M9).

## Decisions

- **`ISourceAdapter` writes into a `ChannelWriter<Sample>`** — `Task RunAsync(ChannelWriter<Sample> writer, CancellationToken ct)`. M3's batcher reads the channel; M7's MQTT adapter implements the same interface (ADR-0006). Rejected: C# events / `IObservable` — no back-pressure.
- **`Sample` lives in `Telemetrix.Contracts`** as an immutable record (ADR-0008). `Value` is `double?`; source adapters convert booleans to 1/0, and strings are out of scope for v1 (D2, accepted in ADR-0010). The status code stays raw (`uint`), and the constructor normalizes both supplied `DateTimeOffset` timestamps to UTC without changing their instants.
- **OPC Foundation stack** (`OPCFoundation.NetStandard.Opc.Ua.Client`). Build `ApplicationConfiguration` in code, not XML. Auto-accept server certificates only while the security mode is None. Session recovery uses the SDK's `SessionReconnectHandler`, which transfers or recreates the subscription.
- **Options**: an `OpcUa` section bound to `OpcUaOptions`, validated on start; tags are an array of `{ Asset, Tag, NodeId }`; env override via the standard `OpcUa__Endpoint` convention.
- **Worker**: `OpcUaIngestionWorker` (`BackgroundService`) runs the adapter plus, for M2 only, a channel reader that logs each sample with structured fields.

## Risks / Trade-offs

- [The server advertises its container hostname in endpoint URLs] → discover endpoints, then replace the host with the configured one (`CoreClientUtils.SelectEndpoint` does this).
- [OPC PLC node names differ between versions] → the mapping lives only in `appsettings.Development.json`; confirm in UaExpert (M1 task 2.1).
