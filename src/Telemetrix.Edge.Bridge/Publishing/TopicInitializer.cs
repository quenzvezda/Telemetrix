using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;

namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class TopicInitializer(IOptions<KafkaOptions> options, ILogger<TopicInitializer> logger) : BackgroundService
{
    private bool ready;
    private readonly TaskCompletionSource readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Development tools can wait explicitly; ingestion can still buffer while Kafka is down.
    public Task Ready => readiness.Task;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        ready = await TryInitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!ready)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            ready = await TryInitializeAsync(stoppingToken);
        }
    }

    private async Task<bool> TryInitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeAsync(cancellationToken);
            readiness.TrySetResult();
            return true;
        }
        catch (KafkaException exception)
        {
            logger.LogWarning(exception, "Kafka topic initialization unavailable; collecting to the edge buffer while retrying");
            return false;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = options.Value.BootstrapServers
        }).Build();
        foreach (var (name, partitions, retention) in new[]
        {
            (KafkaOptions.RawTopic, 6, "604800000"),
            (KafkaOptions.EventsTopic, 3, "2592000000")
        })
        {
            try
            {
                await admin.CreateTopicsAsync([new TopicSpecification
                {
                    Name = name,
                    NumPartitions = partitions,
                    ReplicationFactor = 1,
                    Configs = new Dictionary<string, string> { ["retention.ms"] = retention }
                }], new CreateTopicsOptions
                {
                    RequestTimeout = TimeSpan.FromSeconds(15),
                    OperationTimeout = TimeSpan.FromSeconds(15)
                }).WaitAsync(cancellationToken);
                logger.LogInformation(
                    "Created topic {Topic} with {PartitionCount} partitions and retention {RetentionMs} ms",
                    name, partitions, retention);
            }
            catch (CreateTopicsException exception) when (
                exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                logger.LogInformation("Topic {Topic} already exists; keeping its configuration", name);
            }
        }
    }
}
