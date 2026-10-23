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
    [InlineData(false, KafkaOptions.RawTopic, 6, "604800000")]
    [InlineData(true, KafkaOptions.RawTopic, 6, "604800000")]
    [InlineData(false, KafkaOptions.EventsTopic, 3, "2592000000")]
    [InlineData(true, KafkaOptions.EventsTopic, 3, "2592000000")]
    public async Task StartupCreatesOwnedTopicsAndPreservesExistingConfiguration(bool preExisting,
        string topicName, int partitions, string retention)
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
                Name = topicName,
                NumPartitions = 2,
                ReplicationFactor = 1,
                Configs = new Dictionary<string, string> { ["retention.ms"] = "60000" }
            }]).WaitAsync(ct);
        }

        for (var start = 0; start < 2; start++)
        {
            using var initializer = new TopicInitializer(options, NullLogger<TopicInitializer>.Instance);
            await initializer.StartAsync(ct);
            await initializer.Ready.WaitAsync(ct);
            await initializer.StopAsync(ct);

            var metadata = admin.GetMetadata(topicName, TimeSpan.FromSeconds(10));
            var topic = Assert.Single(metadata.Topics);
            Assert.False(topic.Error.IsError);
            Assert.Equal(preExisting ? 2 : partitions, topic.Partitions.Count);
            var configs = await admin.DescribeConfigsAsync([new ConfigResource
            {
                Name = topicName,
                Type = ResourceType.Topic
            }]).WaitAsync(ct);
            Assert.Equal(preExisting ? "60000" : retention,
                Assert.Single(configs).Entries["retention.ms"].Value);
        }
    }
}
