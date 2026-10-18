using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Publishing;
using Testcontainers.Kafka;

namespace Telemetrix.Integration.Tests;

public class KafkaTopicTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupCreatesTheRawTopicAndPreservesExistingConfiguration(bool preExisting)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var kafka = new KafkaBuilder("apache/kafka:4.3.1")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false").Build();
        await kafka.StartAsync(ct);
        var options = Options.Create(new KafkaOptions { BootstrapServers = kafka.GetBootstrapAddress() });
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = options.Value.BootstrapServers
        }).Build();
        if (preExisting)
        {
            await admin.CreateTopicsAsync([new TopicSpecification
            {
                Name = KafkaOptions.RawTopic,
                NumPartitions = 2,
                ReplicationFactor = 1,
                Configs = new Dictionary<string, string> { ["retention.ms"] = "60000" }
            }]).WaitAsync(ct);
        }

        for (var start = 0; start < 2; start++)
        {
            var initializer = new TopicInitializer(options, NullLogger<TopicInitializer>.Instance);
            await initializer.StartAsync(ct);
            await initializer.StopAsync(ct);

            var metadata = admin.GetMetadata(KafkaOptions.RawTopic, TimeSpan.FromSeconds(10));
            var topic = Assert.Single(metadata.Topics);
            Assert.False(topic.Error.IsError);
            Assert.Equal(preExisting ? 2 : 6, topic.Partitions.Count);
            var configs = await admin.DescribeConfigsAsync([new ConfigResource
            {
                Name = KafkaOptions.RawTopic,
                Type = ResourceType.Topic
            }]).WaitAsync(ct);
            Assert.Equal(preExisting ? "60000" : "604800000",
                Assert.Single(configs).Entries["retention.ms"].Value);
        }
    }
}
