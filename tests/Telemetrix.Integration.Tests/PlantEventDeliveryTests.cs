using System.Net;
using System.Net.Sockets;
using Confluent.Kafka;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using Testcontainers.Kafka;

namespace Telemetrix.Integration.Tests;

public sealed class PlantEventDeliveryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"telemetrix-events-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProductionBridgePublishesOrderedConnectionEventsAndFreshTagsWithinThirtySeconds()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await using var opc = new ContainerBuilder("mcr.microsoft.com/iotedge/opc-plc:2.15.11")
            .WithPortBinding(port, 50000).WithCommand("--pn=50000", "--autoaccept", "--ut")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(50000)
                .UntilMessageIsLogged("OPC UA Server started")).Build();
        await using var kafka = new KafkaBuilder("apache/kafka:4.3.1")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false").Build();
        await Task.WhenAll(opc.StartAsync(ct), kafka.StartAsync(ct));
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.Development.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpcUa:Endpoint"] = new UriBuilder("opc.tcp", opc.Hostname, port).Uri.ToString(),
                ["OpcUa:Security:PkiRoot"] = Path.Combine(directory, "pki"),
                ["Kafka:BootstrapServers"] = kafka.GetBootstrapAddress(),
                ["Buffer:Path"] = Path.Combine(directory, "bridge.db")
            });
        builder.Services.AddOpcUaOptions(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ITelemetryContext, ServiceProviderTelemetryContext>();
        builder.Services.AddSingleton<ISourceAdapter, OpcUaSourceAdapter>();
        builder.Services.AddBridgePipeline(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync(ct);
        try
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = kafka.GetBootstrapAddress(), GroupId = $"plant-events-{Guid.NewGuid():N}",
                EnableAutoCommit = false, EnableAutoOffsetStore = false, AllowAutoCreateTopics = false
            }).Build();
            consumer.Assign(Enumerable.Range(0, 6).Select(partition => new TopicPartitionOffset(
                    KafkaOptions.RawTopic, new Partition(partition), Offset.Beginning))
                .Concat(Enumerable.Range(0, 3).Select(partition => new TopicPartitionOffset(
                    KafkaOptions.EventsTopic, new Partition(partition), Offset.Beginning))));
            var initialAssets = new HashSet<string>();
            while (initialAssets.Count < 2)
            {
                var value = await Task.Run(() => consumer.Consume(ct), ct);
                Assert.Equal(KafkaOptions.RawTopic, value.Topic);
                initialAssets.Add(TelemetryJson.Deserialize(value.Message.Value).AssetId);
            }
            await opc.StopAsync(ct);
            var events = new List<PlantEvent>();
            while (events.Count(value => value.Type == PlantEventType.ConnectionLost) < 2)
            {
                var value = await Task.Run(() => consumer.Consume(ct), ct);
                if (value.Topic == KafkaOptions.EventsTopic)
                    events.Add(TelemetryJson.DeserializePlantEvent(value.Message.Value));
            }
            await opc.StartAsync(ct);
            var ready = TimeProvider.System.GetUtcNow();
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recovery.CancelAfter(TimeSpan.FromSeconds(30));
            var freshTags = new HashSet<(string Asset, string Tag)>();
            while (freshTags.Count < 7 || events.Count(value => value.Type == PlantEventType.Gap) < 2)
            {
                var value = await Task.Run(() => consumer.Consume(recovery.Token), recovery.Token);
                if (value.Topic == KafkaOptions.EventsTopic)
                {
                    var plantEvent = TelemetryJson.DeserializePlantEvent(value.Message.Value);
                    Assert.Equal(plantEvent.AssetId, value.Message.Key);
                    events.Add(plantEvent);
                }
                else
                {
                    var batch = TelemetryJson.Deserialize(value.Message.Value);
                    foreach (var sample in batch.Samples.Where(sample => sample.SourceTimestamp >= ready))
                        freshTags.Add((batch.AssetId, sample.Tag));
                }
            }
            var elapsed = TimeProvider.System.GetUtcNow() - ready;
            Assert.True(elapsed < TimeSpan.FromSeconds(30));
            Assert.Equal(6, events.Count);
            foreach (var asset in events.GroupBy(value => value.AssetId))
            {
                Assert.Equal(new[] { PlantEventType.ConnectionLost, PlantEventType.ConnectionRestored, PlantEventType.Gap },
                    asset.Select(value => value.Type));
                var gap = Assert.Single(asset, value => value.Type == PlantEventType.Gap);
                Assert.True(gap.GapEnd > gap.GapStart);
            }
            output.WriteLine($"NFR-04: seven fresh tags and six Kafka plant events in {elapsed.TotalSeconds:F3} seconds.");
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
