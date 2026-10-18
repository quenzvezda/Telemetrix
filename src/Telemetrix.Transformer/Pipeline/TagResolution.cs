using Telemetrix.Contracts;

namespace Telemetrix.Transformer.Pipeline;

public sealed record ResolvedSample(BatchSample Sample, TagMetadata Tag);

public sealed record TagResolution(IReadOnlyList<ResolvedSample> KnownSamples,
    TelemetryBatch? DeadLetterBatch);
