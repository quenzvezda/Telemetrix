using System.Threading.Channels;
using Confluent.Kafka;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class KafkaBatchPublisher(StoreAndForwardQueue queue,
    ILogger<KafkaBatchPublisher> logger) : IBatchPublisher
{
    public async Task RunAsync(ChannelReader<TelemetryBatch> batches, CancellationToken cancellationToken)
    {
        await foreach (var batch in batches.ReadAllAsync(cancellationToken))
        {
            using var scope = logger.BeginScope(new Dictionary<string, object?>
                { ["batchId"] = batch.BatchId, ["assetId"] = batch.AssetId });
            await queue.PublishAsync(new BufferedEntry(KafkaOptions.RawTopic, batch.AssetId,
                TelemetryJson.Serialize(batch), batch.Samples.Count, batch.CreatedAt), cancellationToken);
            logger.LogInformation(
                "Accepted batch {BatchId} for {AssetId}: samples={SampleCount}, topic={Topic}",
                batch.BatchId, batch.AssetId, batch.Samples.Count, KafkaOptions.RawTopic);
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
