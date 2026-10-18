using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;

namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class TopicInitializer(IOptions<KafkaOptions> options, ILogger<TopicInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = options.Value.BootstrapServers
        }).Build();
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification
            {
                Name = KafkaOptions.RawTopic,
                NumPartitions = 6,
                ReplicationFactor = 1,
                Configs = new Dictionary<string, string> { ["retention.ms"] = "604800000" }
            }], new CreateTopicsOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(15),
                OperationTimeout = TimeSpan.FromSeconds(15)
            }).WaitAsync(cancellationToken);
            logger.LogInformation("Created topic {Topic} with 6 partitions and 7-day retention", KafkaOptions.RawTopic);
        }
        catch (CreateTopicsException exception) when (
            exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            logger.LogInformation("Topic {Topic} already exists; keeping its configuration", KafkaOptions.RawTopic);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
