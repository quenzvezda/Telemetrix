using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Telemetrix.Transformer.Consuming;

namespace Telemetrix.Integration.Tests;

public class InvalidBatchDeliveryTests
{
    [Fact]
    public async Task MalformedBatchBetweenValidBatchesIsDeadLetteredAndDoesNotBlockThePartition()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        using var host = environment.Host();
        try
        {
            await host.StartAsync(ct);
            await environment.ProduceAsync(DeliveryTestEnvironment.Batch(0), 0, ct);
            var payload = "{malformed-batch"u8.ToArray();
            var origin = await environment.Producer.ProduceAsync(
                new TopicPartition(KafkaOptions.RawTopic, new Partition(0)),
                new Message<string, byte[]> { Key = "original-key", Value = payload }, ct);
            await environment.ProduceAsync(DeliveryTestEnvironment.Batch(1), 0, ct);
            await environment.WaitForOffsetsAsync([3], ct);
            Assert.Equal(20, await environment.CountAsync(ct));

            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = environment.Kafka.GetBootstrapAddress(),
                GroupId = Guid.NewGuid().ToString(), EnableAutoCommit = false
            }).Build();
            consumer.Assign(new TopicPartitionOffset(KafkaOptions.DeadLetterTopic, new Partition(0), Offset.Beginning));
            var rejected = await Task.Run(() => consumer.Consume(ct), ct);
            Assert.Equal("original-key", rejected.Message.Key);
            Assert.Equal(payload, rejected.Message.Value);
            Assert.Equal("invalid-json", Header("dlq.reason"));
            Assert.False(string.IsNullOrWhiteSpace(Header("dlq.error")));
            Assert.InRange(rejected.Message.Headers.GetLastBytes("dlq.error").Length, 1, 1024);
            Assert.Equal(origin.Topic, Header("dlq.source.topic"));
            Assert.Equal("0", Header("dlq.source.partition"));
            Assert.Equal("1", Header("dlq.source.offset"));
            Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(Header("dlq.timestamp"), CultureInfo.InvariantCulture).Offset);
            string Header(string name) => Encoding.UTF8.GetString(rejected.Message.Headers.GetLastBytes(name));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StopAsync(cleanup.Token);
        }
    }
}
