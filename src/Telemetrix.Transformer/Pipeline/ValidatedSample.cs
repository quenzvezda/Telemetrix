namespace Telemetrix.Transformer.Pipeline;

public sealed record ValidatedSample(int TagId, DateTimeOffset SourceTimestamp, double? Value, Quality Quality);
