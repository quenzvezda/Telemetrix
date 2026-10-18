using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;

namespace Telemetrix.Transformer.Consuming;

public sealed class DeadLetterTopicInitializer(IOptions<KafkaOptions> options,
    ILogger<DeadLetterTopicInitializer> logger) : IHostedService
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
                Name = KafkaOptions.DeadLetterTopic,
                NumPartitions = 1,
                ReplicationFactor = 1,
                Configs = new Dictionary<string, string> { ["retention.ms"] = "2592000000" }
            }], new CreateTopicsOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(15),
                OperationTimeout = TimeSpan.FromSeconds(15)
            }).WaitAsync(cancellationToken);
            logger.LogInformation("Created topic {Topic} with 1 partition and 30-day retention", KafkaOptions.DeadLetterTopic);
        }
        catch (CreateTopicsException exception) when (
            exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            logger.LogInformation("Topic {Topic} already exists; keeping its configuration", KafkaOptions.DeadLetterTopic);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
