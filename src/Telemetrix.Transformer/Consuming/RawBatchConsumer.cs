using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;

namespace Telemetrix.Transformer.Consuming;

public sealed class RawBatchConsumer(IOptions<KafkaOptions> options, IRawBatchHandler handler,
    ILogger<RawBatchConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Factory.StartNew(
        () => Consume(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Consume(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(CreateConfig(options.Value))
          .SetPartitionsAssignedHandler((_, partitions) => logger.LogInformation(
            "Assigned raw partitions {Partitions} in group {Group}",
            string.Join(",", partitions.Select(partition => partition.Partition.Value)), KafkaOptions.ConsumerGroup))
          .SetPartitionsRevokedHandler((_, partitions) => logger.LogInformation(
              "Revoked raw partitions {Partitions}",
              string.Join(",", partitions.Select(partition => partition.Partition.Value))))
          .Build();
        try
        {
            consumer.Subscribe(KafkaOptions.RawTopic);
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = consumer.Consume(stoppingToken);
                var batch = TelemetryJson.Deserialize(result.Message.Value);
                var delivery = new RawBatchDelivery(batch, result.Message.Key,
                    result.Partition.Value, result.Offset.Value);
                // Keep Consume, callbacks and Close on this dedicated thread; process one batch at a time.
                handler.HandleAsync(delivery, stoppingToken).GetAwaiter().GetResult();
                logger.LogInformation("Handled batch {BatchId} for {AssetId} at partition={Partition}, offset={Offset}",
                    batch.BatchId, batch.AssetId, delivery.Partition, delivery.Offset);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    public static ConsumerConfig CreateConfig(KafkaOptions options) => new()
    {
        BootstrapServers = options.BootstrapServers,
        GroupId = KafkaOptions.ConsumerGroup,
        ClientId = "telemetrix-transformer",
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = false,
        EnableAutoOffsetStore = false,
        AllowAutoCreateTopics = false
    };
}
