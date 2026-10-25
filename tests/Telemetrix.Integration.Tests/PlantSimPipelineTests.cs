using Confluent.Kafka;
using System.Globalization;
using Dapper;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using BridgeKafkaOptions = Telemetrix.Edge.Bridge.Publishing.KafkaOptions;

namespace Telemetrix.Integration.Tests;

[Collection(PlantSimPipelineCollection.Name)]
public sealed class PlantSimPipelineTests(ITestOutputHelper output) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"telemetrix-plantsim-pipeline-{Guid.NewGuid():N}");

    [Fact]
    public async Task ContainerSourceStoresSevenTagsAndPublishesOrderedGapsAfterMethodRestart()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        var root = FindRepository();
        await using var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(root)
            .WithDockerfile("src/Telemetrix.PlantSim/Dockerfile")
            .WithName($"telemetrix-plantsim-test:{Guid.NewGuid():N}").Build();
        await image.CreateAsync(ct);
        await using var simulator = new ContainerBuilder(image)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("PlantSim__SecurityMode", "None")
            .WithEnvironment("PlantSim__Endpoint", "opc.tcp://0.0.0.0:4840")
            .WithEnvironment("Faults__Random__Enabled", "false")
            .WithPortBinding(4840, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("PlantSim OPC UA server started")).Build();
        await using var environment = new DeliveryTestEnvironment();
        await Task.WhenAll(simulator.StartAsync(ct), environment.StartAsync(ct));
        var endpoint = $"opc.tcp://{simulator.Hostname}:{simulator.GetMappedPublicPort(4840)}";
        await using var client = new PlantSimFixture(endpoint);
        using var session = await client.ConnectAsync(ct);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.PlantSim.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpcUa:Endpoint"] = endpoint,
                ["OpcUa:Security:Mode"] = "None",
                ["OpcUa:Security:PkiRoot"] = Path.Combine(directory, "pki"),
                ["Kafka:BootstrapServers"] = environment.Kafka.GetBootstrapAddress(),
                ["Buffer:Path"] = Path.Combine(directory, "bridge.db")
            });
        builder.Services.AddOpcUaOptions(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ITelemetryContext, ServiceProviderTelemetryContext>();
        builder.Services.AddSingleton<ISourceAdapter, OpcUaSourceAdapter>();
        builder.Services.AddBridgePipeline(builder.Configuration);
        using var bridge = builder.Build();
        using var transformer = environment.Host();
        await bridge.StartAsync(ct);
        await transformer.StartAsync(ct);
        try
        {
            await using var connection = await environment.Source.OpenConnectionAsync(ct);
            const string tagsSql = """
                SELECT count(DISTINCT tag_id) FROM measurement
                """;
            while (await connection.ExecuteScalarAsync<int>(new CommandDefinition(tagsSql, cancellationToken: ct)) < 7)
                await Task.Delay(100, ct);
            var counts = (await connection.QueryAsync<(string Asset, string Tag, long Count)>(new CommandDefinition("""
                SELECT t.asset_id, t.name, count(*)
                FROM measurement m JOIN tag t USING (tag_id)
                GROUP BY t.asset_id, t.name ORDER BY t.asset_id, t.name
                """, cancellationToken: ct))).ToArray();
            Assert.Equal(7, counts.Length);
            Assert.All(counts, count => Assert.True(count.Count > 0));
            Assert.Equal(1d, await connection.ExecuteScalarAsync<double>(new CommandDefinition("""
                SELECT m.value FROM measurement m JOIN tag t USING (tag_id)
                WHERE t.name = 'Running' ORDER BY m.time LIMIT 1
                """, cancellationToken: ct)));
            output.WriteLine("M6_CONFIG_PASS: seven tags stored through the unmodified Bridge and Transformer.");

            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = environment.Kafka.GetBootstrapAddress(), GroupId = $"plantsim-review-{Guid.NewGuid():N}",
                EnableAutoCommit = false, EnableAutoOffsetStore = false, AllowAutoCreateTopics = false
            }).Build();
            consumer.Assign(Enumerable.Range(0, 3).Select(partition => new TopicPartitionOffset(
                BridgeKafkaOptions.EventsTopic, new Partition(partition), Offset.Beginning)));
            var restartRequested = TimeProvider.System.GetUtcNow();
            await PlantSimFaultTests.CallAsync(session, "RestartServer", ct, new Variant(5u));
            DateTimeOffset serverReady;
            using (var serverDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                serverDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var logs = await simulator.GetLogsAsync(since: restartRequested.UtcDateTime,
                        timestampsEnabled: true, ct: serverDeadline.Token);
                    var readyLine = logs.Stdout.Split('\n').FirstOrDefault(line =>
                        line.Contains("PlantSim OPC UA server started at", StringComparison.Ordinal));
                    if (readyLine is not null)
                    {
                        serverReady = DateTimeOffset.Parse(readyLine[..readyLine.IndexOf(' ')], CultureInfo.InvariantCulture);
                        break;
                    }
                    await Task.Delay(50, serverDeadline.Token);
                }
            }
            Assert.InRange((serverReady - restartRequested).TotalSeconds, 0, 10);
            List<PlantEvent> events = [];
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recovery.CancelAfter(serverReady.AddSeconds(30) - TimeProvider.System.GetUtcNow());
            while (events.Count(value => value.Type == PlantEventType.Gap) < 2)
            {
                var value = await Task.Run(() => consumer.Consume(recovery.Token), recovery.Token);
                var plantEvent = TelemetryJson.DeserializePlantEvent(value.Message.Value);
                Assert.Equal(plantEvent.AssetId, value.Message.Key);
                events.Add(plantEvent);
            }
            foreach (var asset in events.GroupBy(value => value.AssetId))
            {
                Assert.Equal(new[] { PlantEventType.ConnectionLost, PlantEventType.ConnectionRestored, PlantEventType.Gap },
                    asset.Select(value => value.Type));
                var gap = Assert.Single(asset, value => value.Type == PlantEventType.Gap);
                Assert.True(gap.GapEnd > gap.GapStart);
                Assert.True(gap.OccurredAt <= serverReady.AddSeconds(30));
            }
            Assert.Equal(2, events.Select(value => value.AssetId).Distinct().Count());
            // Every configured tag, including the unchanged Boolean, must resume after the session rebuild.
            while (await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT count(DISTINCT tag_id) FROM measurement WHERE time > @After
                """, new { After = restartRequested }, cancellationToken: recovery.Token)) < 7)
                await Task.Delay(100, recovery.Token);
            output.WriteLine($"M6_RESTART_PASS: seven fresh tags and six ordered events after method restart in " +
                $"{(TimeProvider.System.GetUtcNow() - serverReady).TotalSeconds:F3} seconds from server readiness.");
        }
        catch
        {
            var logs = await simulator.GetLogsAsync(timestampsEnabled: true, ct: ct);
            output.WriteLine(logs.Stdout);
            output.WriteLine(logs.Stderr);
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await bridge.StopAsync(cleanup.Token);
            await transformer.StopAsync(cleanup.Token);
        }
    }

    private static string FindRepository()
    {
        for (var candidate = new DirectoryInfo(AppContext.BaseDirectory); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "Telemetrix.slnx"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Cannot locate the PlantSim Docker build context.");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
