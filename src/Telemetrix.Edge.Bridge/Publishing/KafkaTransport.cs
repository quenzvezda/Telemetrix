using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Edge.Bridge.Batching;

namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class KafkaTransport(IOptions<KafkaOptions> options, BridgeMetrics? metrics = null) : IKafkaTransport, IDisposable
{
    private readonly IProducer<string, byte[]> producer =
        new ProducerBuilder<string, byte[]>(KafkaBatchPublisher.CreateConfig(options.Value)).Build();

    public async Task PublishAsync(BufferedEntry entry, CancellationToken cancellationToken)
    {
        await producer.ProduceAsync(entry.Topic, new Message<string, byte[]>
        {
            Key = entry.AssetKey, Value = entry.Payload
        }, cancellationToken);
        if (entry.Topic == KafkaOptions.RawTopic) metrics?.BatchProduced(entry.AssetKey);
    }

    public void Dispose() => producer.Dispose();
}
