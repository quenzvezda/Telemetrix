using System.Text.Json;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using Testcontainers.Kafka;

namespace Telemetrix.Integration.Tests;

public class KafkaPublishingTests
{
    [Fact]
    public async Task HostDrainsSamplesToKeyedJsonBatchesOnGracefulShutdown()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var kafka = new KafkaBuilder("apache/kafka:4.3.1")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false").Build();
        await kafka.StartAsync(ct);

        var source = new BurstSource();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = kafka.GetBootstrapAddress(),
            ["Batching:MaxSamples"] = "2"
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<OpcUaOptions>(options => options.Endpoint = "opc.tcp://test-source:50000");
        builder.Services.AddSingleton<ISourceAdapter>(source);
        builder.Services.AddBridgePipeline(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync(ct);
        await source.Ready.Task.WaitAsync(ct);
        await host.StopAsync(ct);
        Assert.True(host.Services.GetServices<IHostedService>().OfType<OpcUaIngestionWorker>()
            .Single().ExecuteTask!.IsCompletedSuccessfully);

        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = kafka.GetBootstrapAddress(),
            GroupId = $"publishing-test-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            AllowAutoCreateTopics = false
        }).Build();
        consumer.Assign(Enumerable.Range(0, 6).Select(partition => new TopicPartitionOffset(
            KafkaOptions.RawTopic, new Partition(partition), Offset.Beginning)));
        var received = await Task.Run(() =>
        {
            var results = new List<ConsumeResult<string, byte[]>>();
            while (results.Count < 11) results.Add(consumer.Consume(ct));
            return results;
        }, ct);

        foreach (var result in received)
        {
            var batch = TelemetryJson.Deserialize(result.Message.Value);
            Assert.Equal(KafkaOptions.RawTopic, result.Topic);
            Assert.Equal(batch.AssetId, result.Message.Key);
            Assert.Equal(1, batch.SchemaVersion);
            Assert.Equal(7, batch.BatchId.Version);
            Assert.Equal("opcua", batch.Source);
            Assert.Equal("opc.tcp://test-source:50000", batch.Endpoint);
            Assert.Equal("Plant01", batch.PlantId);
            Assert.Equal("Line01", batch.LineId);
            Assert.Equal(2, batch.Samples.Count);
            using var document = JsonDocument.Parse(result.Message.Value);
            Assert.EndsWith("Z", document.RootElement.GetProperty("createdAt").GetString());
            Assert.All(batch.Samples, sample => Assert.Equal(BurstSource.Timestamp, sample.SourceTimestamp));
        }
        Assert.Equal(11, received.Select(result => TelemetryJson.Deserialize(result.Message.Value).BatchId)
            .Distinct().Count());
        foreach (var asset in received.GroupBy(result => result.Message.Key))
        {
            Assert.Single(asset.Select(result => result.Partition.Value).Distinct());
            var values = asset.SelectMany(result => TelemetryJson.Deserialize(result.Message.Value).Samples)
                .Select(sample => sample.Value);
            Assert.Equal(asset.Key == "Pump01"
                ? Enumerable.Range(0, 20).Select(value => (double?)value)
                : new double?[] { 100, 101 }, values);
        }
        Assert.Equal(10, received.Count(result => result.Message.Key == "Pump01"));
        Assert.Single(received, result => result.Message.Key == "Tank01");
    }

    private sealed class BurstSource : ISourceAdapter
    {
        public static readonly DateTimeOffset Timestamp =
            new(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
        {
            for (var index = 0; index < 20; index++)
            {
                await writer.WriteAsync(new Sample("Plant01", "Line01", "Pump01", "FlowRate", index,
                    Timestamp, Timestamp.AddMilliseconds(4), 0), cancellationToken);
            }
            for (var index = 100; index < 102; index++)
            {
                await writer.WriteAsync(new Sample("Plant01", "Line01", "Tank01", "Temperature", index,
                    Timestamp, Timestamp.AddMilliseconds(4), 0x80000000), cancellationToken);
            }
            Ready.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
