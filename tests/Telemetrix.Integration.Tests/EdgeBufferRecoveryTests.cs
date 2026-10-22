using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using Testcontainers.Kafka;

namespace Telemetrix.Integration.Tests;

public sealed class EdgeBufferRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"telemetrix-edge-{Guid.NewGuid():N}");

    [Fact]
    public async Task PausedKafkaAndBridgeRestartRetainBatchesAndEventsInOrder()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        await using var kafka = new KafkaBuilder("apache/kafka:4.3.1")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false").Build();
        await kafka.StartAsync(ct);
        var bootstrap = kafka.GetBootstrapAddress();
        using (var initializer = new TopicInitializer(Microsoft.Extensions.Options.Options.Create(
                   new KafkaOptions { BootstrapServers = kafka.GetBootstrapAddress() }),
                   Microsoft.Extensions.Logging.Abstractions.NullLogger<TopicInitializer>.Instance))
        {
            await initializer.StartAsync(ct);
            await initializer.Ready.WaitAsync(ct);
            await initializer.StopAsync(ct);
        }
        await kafka.PauseAsync(ct);
        byte[] persisted;
        var plantEvent = new PlantEvent(1, Guid.NewGuid(), PlantEventType.ConnectionLost, "Plant01",
            "Line01", "Pump01", "opc.tcp://test:50000", DateTimeOffset.UtcNow, Reason: "test-outage");
        try
        {
            using (var first = Host(bootstrap, [1, 2]))
            {
                await first.StartAsync(ct);
                Assert.False(first.Services.GetServices<IHostedService>().OfType<TopicInitializer>().Single().Ready.IsCompleted);
                var buffer = first.Services.GetRequiredService<SqliteBuffer>();
                await WaitAsync(() => buffer.Depth.Samples == 2, ct);
                persisted = buffer.ReadOldest()!.Payload;
                await first.Services.GetRequiredService<IPlantEventPublisher>().PublishAsync(plantEvent, ct);
                Assert.Equal(new BufferDepth(3, 2, buffer.Depth.Bytes), buffer.Depth);
                await first.StopAsync(ct);
            }
            using var second = Host(bootstrap, [3]);
            await second.StartAsync(ct);
            var recovered = second.Services.GetRequiredService<SqliteBuffer>();
            await WaitAsync(() => recovered.Depth.Samples == 3, ct);
            Assert.Equal(4, recovered.Depth.Batches);
            await kafka.UnpauseAsync(ct);
            await second.Services.GetServices<IHostedService>().OfType<TopicInitializer>().Single().Ready.WaitAsync(ct);
            await WaitAsync(() => recovered.Depth.Batches == 0, ct);
            await second.StopAsync(ct);

            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = kafka.GetBootstrapAddress(), GroupId = $"edge-recovery-{Guid.NewGuid():N}",
                EnableAutoCommit = false, EnableAutoOffsetStore = false, AllowAutoCreateTopics = false
            }).Build();
            consumer.Assign(Enumerable.Range(0, 6).Select(partition => new TopicPartitionOffset(
                    KafkaOptions.RawTopic, new Partition(partition), Offset.Beginning))
                .Concat(Enumerable.Range(0, 3).Select(partition => new TopicPartitionOffset(
                    KafkaOptions.EventsTopic, new Partition(partition), Offset.Beginning))));
            var results = await Task.Run(() =>
            {
                var batches = new List<ConsumeResult<string, byte[]>>();
                var events = new List<PlantEvent>();
                while (batches.Select(value => TelemetryJson.Deserialize(value.Message.Value).BatchId).Distinct().Count() < 3
                       || !events.Any(value => value.EventId == plantEvent.EventId))
                {
                    var received = consumer.Consume(ct);
                    Assert.Equal("Pump01", received.Message.Key);
                    if (received.Topic == KafkaOptions.RawTopic) batches.Add(received);
                    else events.Add(TelemetryJson.DeserializePlantEvent(received.Message.Value));
                }
                return (batches, events);
            }, ct);
            var raw = results.batches.Select(value => TelemetryJson.Deserialize(value.Message.Value)).ToArray();
            Assert.Equal(new double?[] { 1, 2, 3 }, raw.SelectMany(value => value.Samples).Select(value => value.Value).Distinct());
            Assert.Contains(results.batches, value => value.Message.Value.SequenceEqual(persisted));
            Assert.All(results.events, value => Assert.Equal(plantEvent, value));
            Assert.Equal(3, raw.Select(value => value.BatchId).Distinct().Count());
        }
        finally
        {
            // A paused container must be unpaused before Testcontainers can remove it.
            if (kafka.State == DotNet.Testcontainers.Containers.TestcontainersStates.Paused)
                await kafka.UnpauseAsync(TestContext.Current.CancellationToken);
        }
    }

    private IHost Host(string bootstrap, int[] values)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = bootstrap,
            ["Buffer:Path"] = Path.Combine(directory, "bridge.db"),
            ["Batching:MaxSamples"] = "1"
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<OpcUaOptions>(settings => settings.Endpoint = "opc.tcp://test:50000");
        builder.Services.AddSingleton<ISourceAdapter>(new FixedSource(values));
        builder.Services.AddBridgePipeline(builder.Configuration);
        return builder.Build();
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken ct)
    {
        while (!ready()) await Task.Delay(50, ct);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FixedSource(int[] values) : ISourceAdapter
    {
        public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken ct)
        {
            foreach (var value in values)
            {
                var timestamp = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero).AddSeconds(value);
                await writer.WriteAsync(new Sample("Plant01", "Line01", "Pump01", "FlowRate", value,
                    timestamp, timestamp, 0), ct);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }
}
