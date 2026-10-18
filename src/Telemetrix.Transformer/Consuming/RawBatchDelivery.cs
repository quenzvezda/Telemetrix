using Telemetrix.Contracts;

namespace Telemetrix.Transformer.Consuming;

public sealed record RawBatchDelivery(TelemetryBatch Batch, string Key, int Partition, long Offset);
