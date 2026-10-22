using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Telemetrix.Transformer.Consuming;

public sealed class RawBatchConsumer(IOptions<KafkaOptions> options, RawBatchProcessor processor,
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
                // Keep Consume, callbacks and Close on this dedicated thread; process one batch at a time.
                processor.Process(result, () => consumer.Commit(result), stoppingToken);
                logger.LogInformation("Committed raw batch at partition={Partition}, offset={Offset}",
                    result.Partition.Value, result.Offset.Value);
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
