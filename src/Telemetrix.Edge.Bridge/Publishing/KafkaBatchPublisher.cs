using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class KafkaBatchPublisher(IOptions<KafkaOptions> options,
    ILogger<KafkaBatchPublisher> logger) : IBatchPublisher
{
    public async Task RunAsync(ChannelReader<TelemetryBatch> batches, CancellationToken cancellationToken)
    {
        using var producer = new ProducerBuilder<string, byte[]>(CreateConfig(options.Value)).Build();

        await foreach (var batch in batches.ReadAllAsync(cancellationToken))
        {
            var result = await producer.ProduceAsync(KafkaOptions.RawTopic, new Message<string, byte[]>
            {
                Key = batch.AssetId,
                Value = TelemetryJson.Serialize(batch)
            }, cancellationToken);
            logger.LogInformation(
                "Published batch {BatchId} for {AssetId}: samples={SampleCount}, topic={Topic}, partition={Partition}, offset={Offset}",
                batch.BatchId, batch.AssetId, batch.Samples.Count, result.Topic,
                result.Partition.Value, result.Offset.Value);
        }
    }

    public static ProducerConfig CreateConfig(KafkaOptions options) => new()
    {
        BootstrapServers = options.BootstrapServers,
        ClientId = "telemetrix-bridge",
        Acks = Acks.All,
        EnableIdempotence = true,
        MaxInFlight = 5,
        LingerMs = 5,
        CompressionType = CompressionType.Lz4,
        AllowAutoCreateTopics = false,
        MessageTimeoutMs = 10000
    };
}
