using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Consuming;

public sealed class KafkaDeadLetterPublisher : IDeadLetterPublisher, IDisposable
{
    private readonly IProducer<string, byte[]> producer;

    public KafkaDeadLetterPublisher(IOptions<KafkaOptions> options)
    {
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            AllowAutoCreateTopics = false,
            MessageTimeoutMs = 10000
        }).Build();
    }

    public async Task PublishUnknownTagsAsync(TelemetryBatch batch, CancellationToken cancellationToken)
    {
        await producer.ProduceAsync(KafkaOptions.DeadLetterTopic, new Message<string, byte[]>
        {
            Key = batch.AssetId,
            Value = TelemetryJson.Serialize(batch),
            Headers = new Headers { { "dlq.reason", Encoding.UTF8.GetBytes("unknown-tag") } }
        }, cancellationToken);
    }

    public void Dispose() => producer.Dispose();
}
