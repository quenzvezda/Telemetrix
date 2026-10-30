using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Scripting;
using Telemetrix.Transformer.Storage;
using Polly;

namespace Telemetrix.Transformer.Pipeline;

public sealed class TelemetryBatchHandler(TagResolver resolver, IMeasurementWriter writer,
    IDeadLetterPublisher deadLetterPublisher, ILogger<TelemetryBatchHandler> logger,
    ResiliencePipeline storageRetry, TransformationStage? transformations = null, TransformerMetrics? metrics = null)
    : IRawBatchHandler
{
    public async Task HandleAsync(RawBatchDelivery delivery, CancellationToken cancellationToken)
    {
        var batch = delivery.Batch;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
            { ["batchId"] = batch.BatchId, ["assetId"] = batch.AssetId });
        var resolution = await resolver.ResolveAsync(batch, cancellationToken);
        var validated = resolution.KnownSamples.Select(resolved => new ValidatedSample(resolved.Tag.TagId,
            resolved.Sample.SourceTimestamp, resolved.Sample.Value,
            RangeCheck.Evaluate(resolved.Sample.Value, QualityMapper.Map(resolved.Sample.StatusCode),
                resolved.Tag.MinValid, resolved.Tag.MaxValid))).ToArray();
        // Raw Measurements are final before any script runs; a script only adds a record (ADR-0018).
        var transformation = transformations is null ? null
            : await transformations.RunAsync(batch, resolution.KnownSamples, validated, cancellationToken);

        var inserted = await storageRetry.ExecuteAsync(async ct =>
        {
            try { return await writer.WriteAsync(batch.BatchId, validated, transformation, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                logger.LogError(exception, "Storage failed for batch {BatchId}; retaining the raw offset for retry", batch.BatchId);
                throw;
            }
        }, cancellationToken);
        if (transformation is not null)
        {
            metrics?.TransformationCompleted(transformation);
            logger.LogInformation("Transformation {Label} {ScriptVersion} for batch {BatchId}: outcome={Outcome}, inputs={InputCount}, unchanged={UnchangedCount}, modified={ModifiedCount}, flagged={FlaggedCount}, rejected={RejectedCount}, outputs={OutputCount}, durationMs={DurationMs}, error={Error}",
                transformation.Label, transformation.ScriptVersion, batch.BatchId, transformation.Outcome,
                transformation.InputCount, transformation.UnchangedCount, transformation.ModifiedCount,
                transformation.FlaggedCount, transformation.RejectedCount, transformation.OutputCount,
                Math.Round(transformation.DurationMs, 3), transformation.Error);
        }
        if (resolution.DeadLetterBatch is { } unknown)
            await deadLetterPublisher.PublishUnknownTagsAsync(unknown, cancellationToken);

        logger.LogInformation("Processed batch {BatchId} for {AssetId}: known={KnownCount}, inserted={InsertedCount}, unknown={UnknownCount}",
            batch.BatchId, batch.AssetId, validated.Length, inserted, resolution.DeadLetterBatch?.Samples.Count ?? 0);
    }
}
