using Confluent.Kafka;
using Dapper;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Sources;
using BridgeKafkaOptions = Telemetrix.Edge.Bridge.Publishing.KafkaOptions;

namespace Telemetrix.Integration.Tests;

[Collection(PlantSimPipelineCollection.Name)]
public sealed class MqttPipelineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ContainerSimulatorStoresSevenTagsAndBrokerRestartPublishesOrderedEvents()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync(ct);
        await using var mqtt = new MqttFixture(network);
        await using var environment = new DeliveryTestEnvironment();
        await Task.WhenAll(mqtt.Broker.StartAsync(ct), environment.StartAsync(ct));
        await using var image = new ImageFromDockerfileBuilder().WithDockerfileDirectory(Repository())
            .WithDockerfile("src/Telemetrix.MqttSim/Dockerfile").WithName($"telemetrix-mqttsim-test:{Guid.NewGuid():N}").Build();
        await image.CreateAsync(ct);
        await using var simulator = new ContainerBuilder(image).WithNetwork(network)
            .WithEnvironment("MqttSim__Host", "mqtt")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("MqttSim connected")).Build();
        var directory = Path.Combine(Path.GetTempPath(), $"telemetrix-mqtt-pipeline-{Guid.NewGuid():N}");
        try
        {
            using var bridge = Bridge(environment, mqtt, directory);
            using var transformer = environment.Host();
            await bridge.StartAsync(ct);
            await transformer.StartAsync(ct);
            await simulator.StartAsync(ct);
            try
            {
                await using var connection = await environment.Source.OpenConnectionAsync(ct);
                while (await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT count(DISTINCT tag_id) FROM measurement", cancellationToken: ct)) < 7)
                    await Task.Delay(100, ct);
                Assert.Equal(1d, await connection.ExecuteScalarAsync<double>(new CommandDefinition("""
                    SELECT m.value FROM measurement m JOIN tag t USING(tag_id)
                    WHERE t.name = 'Running' ORDER BY time LIMIT 1
                    """, cancellationToken: ct)));
                output.WriteLine("M7_PIPELINE_PASS: seven tags stored from the production MqttSim container; Running=1.");

                using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
                {
                    BootstrapServers = environment.Kafka.GetBootstrapAddress(), GroupId = Guid.NewGuid().ToString("N"),
                    EnableAutoCommit = false, EnableAutoOffsetStore = false
                }).Build();
                consumer.Assign(Enumerable.Range(0, 3).Select(p => new TopicPartitionOffset(
                    BridgeKafkaOptions.EventsTopic, new Partition(p), Offset.Beginning)));
                var after = DateTimeOffset.UtcNow;
                await mqtt.Broker.StopAsync(ct);
                var events = new List<PlantEvent>();
                while (events.Count(e => e.Type == PlantEventType.ConnectionLost) < 2)
                    events.Add(TelemetryJson.DeserializePlantEvent((await Task.Run(() => consumer.Consume(ct), ct)).Message.Value));
                await mqtt.Broker.StartAsync(ct);
                while (events.Count(e => e.Type == PlantEventType.Gap) < 2)
                    events.Add(TelemetryJson.DeserializePlantEvent((await Task.Run(() => consumer.Consume(ct), ct)).Message.Value));
                foreach (var asset in events.GroupBy(e => e.AssetId))
                {
                    Assert.Equal(new[] { PlantEventType.ConnectionLost, PlantEventType.ConnectionRestored, PlantEventType.Gap },
                        asset.Select(e => e.Type));
                    Assert.All(asset, e => Assert.Equal("source-unavailable", e.Reason));
                    Assert.True(asset.Last().GapEnd > asset.Last().GapStart);
                }
                while (await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT count(DISTINCT tag_id) FROM measurement WHERE time > @After", new { After = after }, cancellationToken: ct)) < 7)
                    await Task.Delay(100, ct);
                output.WriteLine("M7_RESTART_PASS: seven fresh tags and six ordered durable plant.events after broker restart.");

                using var raw = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
                    { BootstrapServers = environment.Kafka.GetBootstrapAddress(), GroupId = Guid.NewGuid().ToString("N") }).Build();
                raw.Assign(Enumerable.Range(0, 6).Select(p => new TopicPartitionOffset(
                    BridgeKafkaOptions.RawTopic, new Partition(p), Offset.Beginning)));
                var batch = TelemetryJson.Deserialize((await Task.Run(() => raw.Consume(ct), ct)).Message.Value);
                Assert.Equal("mqtt", batch.Source);
                Assert.Equal($"mqtt://{mqtt.Host}:{mqtt.Port}", batch.Endpoint);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await simulator.StopAsync(cleanup.Token);
                await bridge.StopAsync(cleanup.Token);
                await transformer.StopAsync(cleanup.Token);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal static IHost Bridge(DeliveryTestEnvironment environment, MqttFixture mqtt, string directory)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Source:Kind"] = "Mqtt", ["Source:Mqtt:Host"] = mqtt.Host, ["Source:Mqtt:Port"] = mqtt.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Source:Mqtt:ClientId"] = Guid.NewGuid().ToString("N"),
            ["Kafka:BootstrapServers"] = environment.Kafka.GetBootstrapAddress(), ["Buffer:Path"] = Path.Combine(directory, "edge.db")
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddBridgeSource(builder.Configuration);
        builder.Services.AddBridgePipeline(builder.Configuration);
        return builder.Build();
    }

    internal static string Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Telemetrix.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Cannot locate the Docker build context.");
    }
}
