using Confluent.Kafka;
using Telemetrix.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Telemetrix.Transformer.Consuming;

public sealed class RawBatchProcessor(IRawBatchHandler handler, IInvalidBatchPublisher invalidBatchPublisher,
    ILogger<RawBatchProcessor>? logger = null)
{
    private readonly ILogger<RawBatchProcessor> log = logger ?? NullLogger<RawBatchProcessor>.Instance;
    // The caller owns Kafka's thread. Complete processing before invoking its commit callback.
    public void Process(ConsumeResult<string, byte[]> result, Action commit, CancellationToken cancellationToken)
    {
        TelemetryBatch batch;
        try { batch = TelemetryJson.DeserializeValidated(result.Message.Value); }
        catch (TelemetryValidationException exception)
        {
            invalidBatchPublisher.PublishAsync(result, exception.Reason, exception.Message, cancellationToken)
                .GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            commit();
            return;
        }
        using var scope = log.BeginScope(new Dictionary<string, object?>
            { ["batchId"] = batch.BatchId, ["assetId"] = batch.AssetId });
        log.LogInformation("Consumed batch {BatchId} at partition={Partition}, offset={Offset}", batch.BatchId, result.Partition.Value, result.Offset.Value);
        var delivery = new RawBatchDelivery(batch, result.Message.Key, result.Partition.Value, result.Offset.Value);
        handler.HandleAsync(delivery, cancellationToken).GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        commit();
        log.LogInformation("Committed batch {BatchId} at partition={Partition}, offset={Offset}", batch.BatchId, result.Partition.Value, result.Offset.Value);
    }
}
