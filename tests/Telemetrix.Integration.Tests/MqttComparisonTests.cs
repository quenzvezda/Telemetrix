using System.Globalization;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.MqttSim;

namespace Telemetrix.Integration.Tests;

[Collection(PlantSimPipelineCollection.Name)]
public sealed class MqttComparisonTests(ITestOutputHelper output)
{
    [Fact]
    public async Task BothProtocolsStoreTheSameTagCountsOverTheSameSourceTimeWindow()
    {
        // CI keeps a short regression; the recorded milestone acceptance uses 600 real seconds.
        var seconds = int.Parse(Environment.GetEnvironmentVariable("TELEMETRIX_M7_COMPARE_SECONDS") ?? "15", CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 10, 600);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 180));
        var ct = deadline.Token;
        await using var mqtt = new MqttFixture();
        await using var plant = new PlantSimFixture();
        await using var mqttEnvironment = new DeliveryTestEnvironment();
        await using var opcEnvironment = new DeliveryTestEnvironment();
        await Task.WhenAll(mqtt.Broker.StartAsync(ct), plant.StartAsync(ct),
            mqttEnvironment.StartAsync(ct), opcEnvironment.StartAsync(ct));
        var directory = Path.Combine(Path.GetTempPath(), $"telemetrix-m7-comparison-{Guid.NewGuid():N}");
        try
        {
            using var mqttBridge = MqttPipelineTests.Bridge(mqttEnvironment, mqtt, Path.Combine(directory, "mqtt"));
            var opcBuilder = Host.CreateApplicationBuilder();
            opcBuilder.Configuration.Sources.Clear();
            opcBuilder.Logging.ClearProviders();
            opcBuilder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"))
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.PlantSim.json"))
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpcUa:Endpoint"] = plant.Endpoint,
                    ["Kafka:BootstrapServers"] = opcEnvironment.Kafka.GetBootstrapAddress(),
                    ["Buffer:Path"] = Path.Combine(directory, "opc", "edge.db")
                });
            opcBuilder.Services.AddSingleton(TimeProvider.System);
            opcBuilder.Services.AddBridgeSource(opcBuilder.Configuration);
            opcBuilder.Services.AddBridgePipeline(opcBuilder.Configuration);
            using var opcBridge = opcBuilder.Build();
            using var mqttTransformer = mqttEnvironment.Host();
            using var opcTransformer = opcEnvironment.Host();
            var simulatorBuilder = Host.CreateApplicationBuilder();
            simulatorBuilder.Logging.ClearProviders();
            simulatorBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MqttSim:Host"] = mqtt.Host, ["MqttSim:Port"] = mqtt.Port.ToString(CultureInfo.InvariantCulture),
                ["MqttSim:ClientId"] = Guid.NewGuid().ToString("N")
            });
            simulatorBuilder.Services.AddMqttSimulator(simulatorBuilder.Configuration);
            using var simulator = simulatorBuilder.Build();
            await mqttBridge.StartAsync(ct);
            await opcBridge.StartAsync(ct);
            await mqttTransformer.StartAsync(ct);
            await opcTransformer.StartAsync(ct);
            await simulator.StartAsync(ct);
            try
            {
                await WaitForSevenAsync(mqttEnvironment, ct);
                await WaitForSevenAsync(opcEnvironment, ct);
                var start = DateTimeOffset.UtcNow.AddSeconds(2);
                var end = start.AddSeconds(seconds);
                output.WriteLine($"M7_COMPARE_WINDOW: [{start:O}, {end:O}); seconds={seconds}");
                await Task.Delay(end.AddSeconds(7) - DateTimeOffset.UtcNow, ct);
                var mqttCounts = await CountsAsync(mqttEnvironment, start, end, ct);
                var opcCounts = await CountsAsync(opcEnvironment, start, end, ct);
                output.WriteLine("M7_COMPARE_MQTT_COUNTS: " + string.Join(", ", mqttCounts.Select(c => $"{c.Tag}={c.Count}")));
                output.WriteLine("M7_COMPARE_OPCUA_COUNTS: " + string.Join(", ", opcCounts.Select(c => $"{c.Tag}={c.Count}")));
                Assert.Equal(7, mqttCounts.Length);
                Assert.Equal(7, opcCounts.Length);
                for (var i = 0; i < mqttCounts.Length; i++)
                {
                    Assert.Equal(opcCounts[i].Tag, mqttCounts[i].Tag);
                    Assert.InRange(mqttCounts[i].Count, seconds - 1, seconds + 1);
                    Assert.InRange(opcCounts[i].Count, seconds - 1, seconds + 1);
                    Assert.InRange(Math.Abs(mqttCounts[i].Count - opcCounts[i].Count), 0, 1);
                    output.WriteLine($"M7_COMPARE_TAG: {mqttCounts[i].Tag}; mqtt={mqttCounts[i].Count}; opcua={opcCounts[i].Count}");
                }
                output.WriteLine($"M7_COMPARE_PASS: seconds={seconds}; mqtt={mqttCounts.Sum(c => c.Count)}; opcua={opcCounts.Sum(c => c.Count)}; tags=7; maxDelta=1");
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await simulator.StopAsync(cleanup.Token);
                await mqttBridge.StopAsync(cleanup.Token);
                await opcBridge.StopAsync(cleanup.Token);
                await mqttTransformer.StopAsync(cleanup.Token);
                await opcTransformer.StopAsync(cleanup.Token);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task WaitForSevenAsync(DeliveryTestEnvironment environment, CancellationToken ct)
    {
        await using var connection = await environment.Source.OpenConnectionAsync(ct);
        while (await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(DISTINCT tag_id) FROM measurement", cancellationToken: ct)) < 7) await Task.Delay(100, ct);
    }

    private static async Task<(string Tag, long Count)[]> CountsAsync(DeliveryTestEnvironment environment,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        await using var connection = await environment.Source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(string Tag, long Count)>(new CommandDefinition("""
            SELECT t.asset_id || '/' || t.name, count(*)
            FROM measurement m JOIN tag t USING(tag_id) WHERE m.time >= @Start AND m.time < @End
            GROUP BY t.asset_id,t.name ORDER BY t.asset_id,t.name
            """, new { Start = start, End = end }, cancellationToken: ct))).ToArray();
    }
}
