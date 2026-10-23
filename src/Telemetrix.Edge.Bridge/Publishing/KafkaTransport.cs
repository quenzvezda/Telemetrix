using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class KafkaTransport(IOptions<KafkaOptions> options) : IKafkaTransport, IDisposable
{
    private readonly IProducer<string, byte[]> producer =
        new ProducerBuilder<string, byte[]>(KafkaBatchPublisher.CreateConfig(options.Value)).Build();

    public async Task PublishAsync(BufferedEntry entry, CancellationToken cancellationToken)
    {
        await producer.ProduceAsync(entry.Topic, new Message<string, byte[]>
        {
            Key = entry.AssetKey, Value = entry.Payload
        }, cancellationToken);
    }

    public void Dispose() => producer.Dispose();
}
