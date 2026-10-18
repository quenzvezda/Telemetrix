using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer.Pipeline;

public sealed class TelemetryBatchHandler(TagResolver resolver, IMeasurementWriter writer,
    IDeadLetterPublisher deadLetterPublisher, ILogger<TelemetryBatchHandler> logger) : IRawBatchHandler
{
    public async Task HandleAsync(RawBatchDelivery delivery, CancellationToken cancellationToken)
    {
        var batch = delivery.Batch;
        var resolution = await resolver.ResolveAsync(batch, cancellationToken);
        var validated = resolution.KnownSamples.Select(resolved => new ValidatedSample(resolved.Tag.TagId,
            resolved.Sample.SourceTimestamp, resolved.Sample.Value,
            RangeCheck.Evaluate(resolved.Sample.Value, QualityMapper.Map(resolved.Sample.StatusCode),
                resolved.Tag.MinValid, resolved.Tag.MaxValid))).ToArray();

        var inserted = await writer.WriteAsync(batch.BatchId, validated, cancellationToken);
        if (resolution.DeadLetterBatch is { } unknown)
            await deadLetterPublisher.PublishUnknownTagsAsync(unknown, cancellationToken);

        logger.LogInformation("Processed batch {BatchId} for {AssetId}: known={KnownCount}, inserted={InsertedCount}, unknown={UnknownCount}",
            batch.BatchId, batch.AssetId, validated.Length, inserted, resolution.DeadLetterBatch?.Samples.Count ?? 0);
    }
}
