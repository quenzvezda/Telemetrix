using Confluent.Kafka;

namespace Telemetrix.Transformer.Consuming;

public interface IInvalidBatchPublisher
{
    Task PublishAsync(ConsumeResult<string, byte[]> result, string reason, string error, CancellationToken cancellationToken);
}
