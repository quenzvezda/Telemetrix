using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using DotNet.Testcontainers.Builders;
using Npgsql;
using Telemetrix.Edge.Bridge;
using Telemetrix.Transformer;

namespace Telemetrix.Integration.Tests;

[Collection(PlantSimPipelineCollection.Name)]
public class DashboardTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FreshGrafanaShowsTrendsPumpTripAndSixtySecondSourceGap()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(Environment.GetEnvironmentVariable("M8_REVIEW_UI") == "1" ? 15 : 6));
        var ct = deadline.Token;
        var root = FindRepository();
        var directory = Path.Combine(Path.GetTempPath(), $"telemetrix-dashboard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync(ct);
        await using var environment = new DeliveryTestEnvironment(network);
        await environment.StartAsync(ct);
        await RollupMigrationTests.MigrateAsync(environment.Source, ct);
        await using (var migration = environment.Source.CreateCommand(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "sql", "migrations", "002_plant_events.sql"), ct)))
            await migration.ExecuteNonQueryAsync(ct);

        var database = new NpgsqlConnectionStringBuilder(environment.Database.GetConnectionString());
        var provisioning = Path.Combine(AppContext.BaseDirectory, "grafana");
        var datasource = (await File.ReadAllTextAsync(Path.Combine(provisioning, "datasources", "timescaledb.yaml"), ct))
            .Replace("user: telemetry", $"user: {database.Username}", StringComparison.Ordinal)
            .Replace("database: telemetry", $"database: {database.Database}", StringComparison.Ordinal);
        await using var grafana = new ContainerBuilder("grafana/grafana:13.2.4")
            .WithNetwork(network).WithPortBinding(3000, true)
            .WithEnvironment("POSTGRES_PASSWORD", database.Password!)
            .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
            .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Admin")
            .WithResourceMapping(Encoding.UTF8.GetBytes(datasource), "/etc/grafana/provisioning/datasources/timescaledb.yaml")
            .WithResourceMapping(new DirectoryInfo(Path.Combine(provisioning, "dashboards")), "/etc/grafana/provisioning/dashboards")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(3000).ForPath("/api/health"))).Build();
        await grafana.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri($"http://{grafana.Hostname}:{grafana.GetMappedPublicPort(3000)}") };
        using var dashboard = JsonDocument.Parse(await http.GetStringAsync("/api/dashboards/uid/telemetrix", ct));
        Assert.Equal("telemetrix", dashboard.RootElement.GetProperty("dashboard").GetProperty("uid").GetString());
        var panels = dashboard.RootElement.GetProperty("dashboard").GetProperty("panels");
        var trendSql = PanelSql(panels, 1);
        var stateSql = PanelSql(panels, 3);
        var gapSql = PanelSql(panels, 4);

        await using var image = new ImageFromDockerfileBuilder().WithDockerfileDirectory(root)
            .WithDockerfile("src/Telemetrix.PlantSim/Dockerfile").WithName($"telemetrix-dashboard-plantsim:{Guid.NewGuid():N}").Build();
        await image.CreateAsync(ct);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await using var plant = new ContainerBuilder(image).WithPortBinding(port, 4840)
            .WithEnvironment("PlantSim__Endpoint", "opc.tcp://0.0.0.0:4840")
            .WithEnvironment("Faults__Random__Enabled", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("PlantSim OPC UA server started")).Build();
        await plant.StartAsync(ct);
        var endpoint = $"opc.tcp://{plant.Hostname}:{port}";
        {
            await using var bridge = BridgeApplication.Build(["--environment=PlantSim", "--Http:Port=0",
                $"--contentRoot={Path.Combine(AppContext.BaseDirectory, "config")}", $"--OpcUa:Endpoint={endpoint}",
                $"--Kafka:BootstrapServers={environment.Kafka.GetBootstrapAddress()}",
                $"--Buffer:Path={Path.Combine(directory, "bridge.db")}", "--Logging:LogLevel:Default=Warning"]);
            await using var transformer = TransformerApplication.Build(["--Http:Port=0", "--PlantEvents:Enabled=true",
                $"--Kafka:BootstrapServers={environment.Kafka.GetBootstrapAddress()}",
                $"--ConnectionStrings:Telemetry={environment.Database.GetConnectionString()}", "--Logging:LogLevel:Default=Warning"]);
            await transformer.StartAsync(ct);
            await bridge.StartAsync(ct);
            try
            {
                await using var connection = await environment.Source.OpenConnectionAsync(ct);
                while (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(DISTINCT tag_id) FROM measurement", cancellationToken: ct)) != 7)
                    await Task.Delay(100, ct);
                using (var trend = await QueryAsync(http, trendSql, "time_series", TimeSpan.FromMinutes(15), ct)) AssertFramesHaveValues(trend);
                await using var client = new PlantSimFixture(endpoint);
                using (var session = await client.ConnectAsync(ct))
                {
                    var tripAt = DateTimeOffset.UtcNow;
                    await PlantSimFaultTests.CallAsync(session, "TripPump", ct);
                    using var tripDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tripDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                    while (await connection.ExecuteScalarAsync<double>(new CommandDefinition("""
                        SELECT m.value FROM measurement m JOIN tag t USING(tag_id)
                        WHERE t.asset_id='Pump01' AND t.name='Running' ORDER BY m.time DESC LIMIT 1
                        """, cancellationToken: tripDeadline.Token)) != 0) await Task.Delay(100, tripDeadline.Token);
                    using var state = await QueryAsync(http, stateSql, "table", TimeSpan.FromMinutes(15), tripDeadline.Token);
                    Assert.Contains("Stopped", state.RootElement.GetRawText());
                    output.WriteLine($"M8_PUMP_PASS: Grafana data reports Stopped in {(DateTimeOffset.UtcNow-tripAt).TotalSeconds:F3}s (refresh=10s).");
                }
                await plant.StopAsync(ct);
                var outage = System.Diagnostics.Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                await plant.StartAsync(ct);
                while (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM plant_event WHERE event_type='Gap'", cancellationToken: ct)) < 2)
                    await Task.Delay(100, ct);
                var duration = await connection.ExecuteScalarAsync<double>(new CommandDefinition(
                    "SELECT extract(epoch FROM gap_end-gap_start) FROM plant_event WHERE asset_id='Pump01' AND event_type='Gap' ORDER BY occurred_at DESC LIMIT 1", cancellationToken: ct));
                Assert.True(duration >= 60);
                using (var gaps = await QueryAsync(http, gapSql, "table", TimeSpan.FromMinutes(15), ct)) AssertFramesHaveValues(gaps);
                using (var trend = await QueryAsync(http, trendSql, "time_series", TimeSpan.FromMinutes(15), ct))
                    Assert.Contains(Frames(trend).SelectMany(frame => frame.GetProperty("data").GetProperty("values").EnumerateArray())
                        .SelectMany(column => column.EnumerateArray()), value => value.ValueKind == JsonValueKind.Null);
                await connection.ExecuteAsync(new CommandDefinition("CALL refresh_continuous_aggregate('measurement_1m',now()-interval '15 minutes',now()+interval '1 minute')", cancellationToken: ct));
                using (var trend = await QueryAsync(http, trendSql, "time_series", TimeSpan.FromDays(7), ct)) AssertFramesHaveValues(trend);
                output.WriteLine($"M8_GAP_PASS: Grafana queries show a {duration:F3}s Pump01 gap, null trend markers and seven-day rollups after {outage.Elapsed.TotalSeconds:F3}s recovery run.");
                if (Environment.GetEnvironmentVariable("M8_REVIEW_UI") == "1")
                {
                    var release = Path.Combine(directory, "release-ui");
                    await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "m8-dashboard-url.json"),
                        JsonSerializer.Serialize(new { url = http.BaseAddress + "d/telemetrix/plant-telemetry?orgId=1", releaseFile = release }), ct);
                    output.WriteLine("M8_UI_READY: disposable Grafana remains available for visual review.");
                    while (!File.Exists(release)) await Task.Delay(500, ct);
                }
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await bridge.StopAsync(cleanup.Token);
                await transformer.StopAsync(cleanup.Token);
            }
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static string PanelSql(JsonElement panels, int id) => panels.EnumerateArray().Single(panel => panel.GetProperty("id").GetInt32() == id)
        .GetProperty("targets")[0].GetProperty("rawSql").GetString()!;

    internal static async Task<JsonDocument> QueryAsync(HttpClient http, string sql, string format, TimeSpan range, CancellationToken ct)
    {
        sql = sql.Replace("${asset:sqlstring}", "'Pump01'", StringComparison.Ordinal)
            .Replace("${tag:sqlstring}", "'FlowRate'", StringComparison.Ordinal)
            .Replace("${__interval}", "1 second", StringComparison.Ordinal);
        var now = DateTimeOffset.UtcNow;
        using var response = await http.PostAsJsonAsync("/api/ds/query", new
        {
            from = (now-range).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            to = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            queries = new[] { new { refId = "A", datasource = new { uid = "timescaledb", type = "grafana-postgresql-datasource" },
                rawSql = sql, format, intervalMs = 1000, maxDataPoints = 1000 } }
        }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, text);
        var result = JsonDocument.Parse(text);
        Assert.False(result.RootElement.GetProperty("results").GetProperty("A").TryGetProperty("error", out _), text);
        return result;
    }

    private static IEnumerable<JsonElement> Frames(JsonDocument result) => result.RootElement.GetProperty("results").GetProperty("A").GetProperty("frames").EnumerateArray();
    private static void AssertFramesHaveValues(JsonDocument result) => Assert.Contains(Frames(result),
        frame => frame.GetProperty("data").GetProperty("values").EnumerateArray().Any(column => column.GetArrayLength() > 0));
    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Telemetrix.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Cannot find the dashboard provisioning root.");
    }
}
