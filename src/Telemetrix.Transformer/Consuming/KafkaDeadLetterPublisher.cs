using System.Text;
using System.Globalization;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Consuming;

public sealed class KafkaDeadLetterPublisher : IDeadLetterPublisher, IInvalidBatchPublisher, IDisposable
{
    private readonly IProducer<string, byte[]> producer;
    private readonly TimeProvider timeProvider;

    public KafkaDeadLetterPublisher(IOptions<KafkaOptions> options, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            AllowAutoCreateTopics = false,
            MessageTimeoutMs = 10000
        }).Build();
    }

    public async Task PublishAsync(ConsumeResult<string, byte[]> result, string reason, string error,
        CancellationToken cancellationToken)
    {
        await producer.ProduceAsync(KafkaOptions.DeadLetterTopic,
            CreateInvalidMessage(result, reason, error, timeProvider.GetUtcNow()), cancellationToken);
    }

    public static Message<string, byte[]> CreateInvalidMessage(ConsumeResult<string, byte[]> result,
        string reason, string error, DateTimeOffset timestamp)
    {
        var errorBytes = Encoding.UTF8.GetBytes(error);
        if (errorBytes.Length > 1024)
        {
            var length = 1024;
            while ((errorBytes[length] & 0xC0) == 0x80) length--;
            errorBytes = errorBytes[..length];
        }
        return new Message<string, byte[]>
        {
            Key = result.Message.Key,
            Value = result.Message.Value,
            Headers = new Headers
            {
                { "dlq.reason", Encoding.UTF8.GetBytes(reason) },
                { "dlq.error", errorBytes },
                { "dlq.source.topic", Encoding.UTF8.GetBytes(result.Topic) },
                { "dlq.source.partition", Encoding.UTF8.GetBytes(result.Partition.Value.ToString(CultureInfo.InvariantCulture)) },
                { "dlq.source.offset", Encoding.UTF8.GetBytes(result.Offset.Value.ToString(CultureInfo.InvariantCulture)) },
                { "dlq.timestamp", Encoding.UTF8.GetBytes(timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) }
            }
        };
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
