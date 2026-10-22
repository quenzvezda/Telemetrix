using System.Threading.Channels;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;
using Testcontainers.Kafka;

namespace Telemetrix.Integration.Tests;

public class RawBatchConsumerTests
{
    [Fact]
    public async Task TwoConsumersSplitAllSixPartitionsAndTheSurvivorTakesOver()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var kafka = new KafkaBuilder("apache/kafka:4.3.1")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false").Build();
        await kafka.StartAsync(ct);
        var options = Options.Create(new KafkaOptions { BootstrapServers = kafka.GetBootstrapAddress() });
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers }).Build();
        await CreateRawTopicAsync(admin, ct);
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers, Acks = Acks.All
        }).Build();
        var firstHandler = new CapturingHandler();
        var secondHandler = new CapturingHandler();
        using var invalidPublisher = new KafkaDeadLetterPublisher(options, TimeProvider.System);
        using var first = new RawBatchConsumer(options, new RawBatchProcessor(firstHandler, invalidPublisher), NullLogger<RawBatchConsumer>.Instance);
        using var second = new RawBatchConsumer(options, new RawBatchProcessor(secondHandler, invalidPublisher), NullLogger<RawBatchConsumer>.Instance);
        try
        {
            await first.StartAsync(ct);
            await second.StartAsync(ct);
            var group = await ConsumerGroupReadiness.WaitForMembersAsync(admin, 2, ct);
            Assert.Equal(KafkaOptions.ConsumerGroup, group.GroupId);
            Assert.All(group.Members, member => Assert.Equal(3, member.Assignment.TopicPartitions.Count));
            var assigned = group.Members.SelectMany(member => member.Assignment.TopicPartitions).ToArray();
            Assert.Equal(6, assigned.Distinct().Count());
            Assert.All(assigned, partition => Assert.Equal(KafkaOptions.RawTopic, partition.Topic));
            Assert.Equal(Enumerable.Range(0, 6), assigned.Select(partition => partition.Partition.Value).Order());

            var expected = new Dictionary<Guid, TelemetryBatch>();
            for (var sequence = 0; sequence < 3; sequence++)
            {
                for (var partition = 0; partition < 6; partition++)
                {
                    var batch = Batch(partition, sequence);
                    expected.Add(batch.BatchId, batch);
                    await producer.ProduceAsync(new TopicPartition(KafkaOptions.RawTopic, new Partition(partition)),
                        new Message<string, byte[]> { Key = batch.AssetId, Value = TelemetryJson.Serialize(batch) }, ct);
                }
            }
            var firstDeliveries = await ReadAsync(firstHandler.Deliveries.Reader, 9, ct);
            var secondDeliveries = await ReadAsync(secondHandler.Deliveries.Reader, 9, ct);
            Assert.Empty(firstDeliveries.Select(delivery => delivery.Partition).Distinct()
                .Intersect(secondDeliveries.Select(delivery => delivery.Partition).Distinct()));
            var received = firstDeliveries.Concat(secondDeliveries).ToArray();
            Assert.Equal(18, received.Select(delivery => delivery.Batch.BatchId).Distinct().Count());
            foreach (var delivery in received)
            {
                var batch = expected[delivery.Batch.BatchId];
                Assert.Equal(batch.AssetId, delivery.Key);
                Assert.Equal(batch.Samples, delivery.Batch.Samples);
                Assert.Equal(batch.CreatedAt, delivery.Batch.CreatedAt);
            }
            foreach (var partition in received.GroupBy(delivery => delivery.Partition))
            {
                Assert.Equal(new long[] { 0, 1, 2 }, partition.Select(delivery => delivery.Offset));
                Assert.Equal(new double?[] { 0, 1, 2 }, partition.Select(delivery => delivery.Batch.Samples[0].Value));
            }

            await first.StopAsync(ct);
            Assert.True(first.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Equal(6, Assert.Single((await ConsumerGroupReadiness.WaitForMembersAsync(admin, 1, ct)).Members)
                .Assignment.TopicPartitions.Count);
            var takeover = Batch(0, 99);
            await producer.ProduceAsync(new TopicPartition(KafkaOptions.RawTopic, new Partition(0)),
                new Message<string, byte[]> { Key = takeover.AssetId, Value = TelemetryJson.Serialize(takeover) }, ct);
            RawBatchDelivery next;
            do { next = await secondHandler.Deliveries.Reader.ReadAsync(ct); }
            while (next.Batch.BatchId != takeover.BatchId);
            Assert.Equal(99, next.Batch.Samples[0].Value);
            await second.StopAsync(ct);
            Assert.True(second.ExecuteTask!.IsCompletedSuccessfully);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Task.WhenAll(first.StopAsync(cleanup.Token), second.StopAsync(cleanup.Token));
        }
    }

    [Fact]
    public async Task HandlerFailureFaultsTheConsumerInsteadOfSkippingTheBatch()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var kafka = new KafkaBuilder("apache/kafka:4.3.1").Build();
        await kafka.StartAsync(ct);
        var options = Options.Create(new KafkaOptions { BootstrapServers = kafka.GetBootstrapAddress() });
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers }).Build();
        await CreateRawTopicAsync(admin, ct);
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = options.Value.BootstrapServers }).Build();
        var batch = Batch(0, 1);
        await producer.ProduceAsync(KafkaOptions.RawTopic,
            new Message<string, byte[]> { Key = batch.AssetId, Value = TelemetryJson.Serialize(batch) }, ct);
        var failure = new InvalidOperationException("handler failed");
        using var invalidPublisher = new KafkaDeadLetterPublisher(options, TimeProvider.System);
        using var consumer = new RawBatchConsumer(options, new RawBatchProcessor(new FailingHandler(failure), invalidPublisher), NullLogger<RawBatchConsumer>.Instance);
        try
        {
            await consumer.StartAsync(ct);
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.ExecuteTask!.WaitAsync(ct));
            Assert.Same(failure, observed);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await consumer.StopAsync(cleanup.Token);
        }
    }

    private static Task CreateRawTopicAsync(IAdminClient admin, CancellationToken ct) => admin.CreateTopicsAsync(
        [new TopicSpecification { Name = KafkaOptions.RawTopic, NumPartitions = 6, ReplicationFactor = 1 }]).WaitAsync(ct);

    private static async Task<List<RawBatchDelivery>> ReadAsync(ChannelReader<RawBatchDelivery> reader, int count, CancellationToken ct)
    {
        var result = new List<RawBatchDelivery>();
        while (result.Count < count) result.Add(await reader.ReadAsync(ct));
        return result;
    }

    private static TelemetryBatch Batch(int partition, int sequence)
    {
        var timestamp = new DateTimeOffset(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);
        return new(1, Guid.NewGuid(), "opcua", "opc.tcp://test:50000", "Plant01", "Line01", $"Asset{partition}",
            timestamp, [new BatchSample("FlowRate", sequence, timestamp.AddSeconds(sequence), timestamp, 0)]);
    }

    private sealed class CapturingHandler : IRawBatchHandler
    {
        public Channel<RawBatchDelivery> Deliveries { get; } = Channel.CreateUnbounded<RawBatchDelivery>();
        public Task HandleAsync(RawBatchDelivery delivery, CancellationToken cancellationToken) =>
            Deliveries.Writer.WriteAsync(delivery, cancellationToken).AsTask();
    }

    private sealed class FailingHandler(Exception failure) : IRawBatchHandler
    {
        public Task HandleAsync(RawBatchDelivery delivery, CancellationToken cancellationToken) => Task.FromException(failure);
    }
}
