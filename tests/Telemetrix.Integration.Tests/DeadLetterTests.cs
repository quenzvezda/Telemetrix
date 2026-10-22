using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;
using Testcontainers.Kafka;

namespace Telemetrix.Integration.Tests;

public class DeadLetterTests
{
    [Fact]
    public async Task StartupCreatesDlqAndUnknownTagsKeepTheirBatchIdAndReason()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var kafka = new KafkaBuilder("apache/kafka:4.3.1")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false").Build();
        await kafka.StartAsync(ct);
        var options = Options.Create(new KafkaOptions { BootstrapServers = kafka.GetBootstrapAddress() });
        var initializer = new DeadLetterTopicInitializer(options, NullLogger<DeadLetterTopicInitializer>.Instance);
        await initializer.StartAsync(ct);
        await initializer.StartAsync(ct);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers }).Build();
        Assert.Single(Assert.Single(admin.GetMetadata(KafkaOptions.DeadLetterTopic, TimeSpan.FromSeconds(10)).Topics).Partitions);
        var config = Assert.Single(await admin.DescribeConfigsAsync([new ConfigResource
        {
            Type = ResourceType.Topic, Name = KafkaOptions.DeadLetterTopic
        }]).WaitAsync(ct));
        Assert.Equal("2592000000", config.Entries["retention.ms"].Value);

        var timestamp = new DateTimeOffset(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);
        var batch = new TelemetryBatch(1, Guid.NewGuid(), "opcua", "opc.tcp://test:50000", "Plant01", "Line01",
            "Pump01", timestamp, [new BatchSample("Torque", 42, timestamp, timestamp, 0)]);
        using var publisher = new KafkaDeadLetterPublisher(options, TimeProvider.System);
        await publisher.PublishUnknownTagsAsync(batch, ct);
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers, GroupId = Guid.NewGuid().ToString(), EnableAutoCommit = false
        }).Build();
        consumer.Assign(new TopicPartitionOffset(KafkaOptions.DeadLetterTopic, new Partition(0), Offset.Beginning));
        var received = await Task.Run(() => consumer.Consume(ct), ct);
        Assert.Equal("Pump01", received.Message.Key);
        Assert.Equal("unknown-tag", Encoding.UTF8.GetString(received.Message.Headers.GetLastBytes("dlq.reason")));
        var restored = TelemetryJson.Deserialize(received.Message.Value);
        Assert.Equal(batch.BatchId, restored.BatchId);
        Assert.Equal(batch.Samples, restored.Samples);
    }
}
