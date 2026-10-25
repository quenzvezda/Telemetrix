using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Telemetrix.Transformer.Consuming;

public sealed class RawBatchConsumer(IOptions<KafkaOptions> options, RawBatchProcessor processor,
    ILogger<RawBatchConsumer> logger, ConsumerAssignments? assignments = null) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Factory.StartNew(
        () => Consume(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Consume(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(CreateConfig(options.Value))
          .SetPartitionsAssignedHandler((_, partitions) =>
          {
              assignments?.Set(partitions);
              logger.LogInformation("Assigned raw partitions {Partitions} in group {Group}",
                  string.Join(",", partitions.Select(partition => partition.Partition.Value)), KafkaOptions.ConsumerGroup);
          })
          .SetPartitionsRevokedHandler((_, partitions) =>
          {
              assignments?.Set([]);
              logger.LogInformation("Revoked raw partitions {Partitions}; completed batches were already committed",
                  string.Join(",", partitions.Select(partition => partition.Partition.Value)));
          })
          .SetPartitionsLostHandler((_, partitions) =>
          {
              assignments?.Set([]);
              logger.LogWarning("Lost raw partitions {Partitions}; leaving offsets unchanged for replay",
                  string.Join(",", partitions.Select(partition => partition.Partition.Value)));
          })
          .Build();
        try
        {
            consumer.Subscribe(KafkaOptions.RawTopic);
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = consumer.Consume(stoppingToken);
                // Keep Consume, callbacks and Close on this dedicated thread; process one batch at a time.
                processor.Process(result, () => consumer.Commit(result), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            assignments?.Set([]);
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
