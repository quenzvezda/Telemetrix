using Confluent.Kafka;
using Telemetrix.Contracts;

namespace Telemetrix.Transformer.Consuming;

public sealed class RawBatchProcessor(IRawBatchHandler handler)
{
    // The caller owns Kafka's thread. Complete processing before invoking its commit callback.
    public void Process(ConsumeResult<string, byte[]> result, Action commit, CancellationToken cancellationToken)
    {
        var batch = TelemetryJson.Deserialize(result.Message.Value);
        var delivery = new RawBatchDelivery(batch, result.Message.Key, result.Partition.Value, result.Offset.Value);
        handler.HandleAsync(delivery, cancellationToken).GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        commit();
    }
}
