using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Consuming;

public sealed class ConsumerLagSampler(IOptions<KafkaOptions> options, ConsumerAssignments assignments,
    TransformerMetrics metrics, TimeProvider timeProvider, ILogger<ConsumerLagSampler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = RawBatchConsumer.CreateConfig(options.Value);
        config.ClientId = "telemetrix-lag-inspector";
        // This separate handle never subscribes or joins the group. It cannot race raw consumption.
        using var inspector = new ConsumerBuilder<string, byte[]>(config).Build();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider);
        try
        {
            do
            {
                try { await Task.Run(() => Sample(inspector), stoppingToken); }
                catch (KafkaException exception)
                {
                    logger.LogWarning("Consumer lag sampling unavailable: {Reason}", exception.Error.Code);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void Sample(IConsumer<string, byte[]> inspector)
    {
        var partitions = assignments.Snapshot();
        if (partitions.Length == 0) { metrics.UpdateLag([]); return; }
        var committed = inspector.Committed(partitions, TimeSpan.FromSeconds(2));
        var values = new List<KeyValuePair<int, long>>();
        foreach (var partition in committed)
        {
            var watermark = inspector.QueryWatermarkOffsets(partition.TopicPartition, TimeSpan.FromSeconds(2));
            var next = partition.Offset == Offset.Unset ? watermark.Low.Value : partition.Offset.Value;
            values.Add(new(partition.Partition.Value, Math.Max(0, watermark.High.Value - next)));
        }
        // A concurrent rebalance invalidates this sample; the next tick uses the new assignment.
        if (partitions.SequenceEqual(assignments.Snapshot())) metrics.UpdateLag(values);
    }
}
