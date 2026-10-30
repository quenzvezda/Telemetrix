using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Polly;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Scripting;

// Decides whether a Batch is transformed: labeled, enabled and not already recorded (ADR-0017, ADR-0018).
public sealed class TransformationStage(IOptions<TransformationOptions> options, ResiliencePipeline retry,
    ILogger<TransformationStage> logger, TransformerMetrics? metrics = null, TransformationEngine? engine = null,
    ITransformationRunStore? runs = null)
{
    public const string Disabled = "disabled";
    public const string AlreadyRecorded = "already-recorded";
    private readonly ConcurrentDictionary<string, bool> warned = new(StringComparer.Ordinal);

    public async Task<TransformationRecord?> RunAsync(TelemetryBatch batch, IReadOnlyList<ResolvedSample> inputs,
        IReadOnlyList<ValidatedSample> validated, CancellationToken cancellationToken)
    {
        if (batch.Transformation is not { } label) return null;
        if (!options.Value.Enabled || engine is null || runs is null)
        {
            metrics?.TransformationSkipped(Disabled);
            if (warned.TryAdd(label, true))
                logger.LogWarning("Scripting is disabled; batches labeled {Label} use the built-in pipeline only", label);
            return null;
        }

        return await retry.ExecuteAsync(async token =>
        {
            try
            {
                if (await runs.ExistsAsync(batch.BatchId, label, token))
                {
                    metrics?.TransformationSkipped(AlreadyRecorded);
                    logger.LogInformation("Batch {BatchId} already has a {Label} transformation record; keeping it", batch.BatchId, label);
                    return null;
                }
                return await engine.TransformAsync(batch, label, inputs, validated, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                logger.LogError(exception, "Transformation {Label} unavailable for batch {BatchId}; retaining the raw offset for retry",
                    label, batch.BatchId);
                throw;
            }
        }, cancellationToken);
    }
}
