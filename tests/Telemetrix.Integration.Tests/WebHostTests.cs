using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Transformer;

namespace Telemetrix.Integration.Tests;

public class WebHostTests
{
    [Fact]
    public async Task BridgeAndTransformerKeepTheirPipelineWithHttpHosts()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await using var mqtt = new MqttFixture();
        await Task.WhenAll(environment.StartAsync(ct), mqtt.Broker.StartAsync(ct));
        var buffer = Path.Combine(Path.GetTempPath(), $"telemetrix-web-{Guid.NewGuid():N}.db");
        try
        {
            await using var transformer = TransformerApplication.Build([
                "--Http:Port=0", $"--Kafka:BootstrapServers={environment.Kafka.GetBootstrapAddress()}",
                $"--ConnectionStrings:Telemetry={environment.Database.GetConnectionString()}"]);
            await using var bridge = BridgeApplication.Build([
                "--Http:Port=0", "--Source:Kind=Mqtt", $"--Source:Mqtt:Host={mqtt.Host}",
                $"--Source:Mqtt:Port={mqtt.Port}", $"--Source:Mqtt:ClientId=web-{Guid.NewGuid():N}",
                $"--Kafka:BootstrapServers={environment.Kafka.GetBootstrapAddress()}",
                $"--Buffer:Path={buffer}", "--Batching:MaxSamples=1"]);
            await transformer.StartAsync(ct);
            await bridge.StartAsync(ct);
            using var http = new HttpClient();
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Address(bridge), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Address(transformer), ct)).StatusCode);
            using var publisher = await mqtt.ConnectAsync(ct);
            // Retained publication also covers the source subscribing after HTTP startup.
            await MqttFixture.PublishAsync(publisher, DateTimeOffset.UtcNow, ct, retain: true);
            await environment.WaitForCountAsync(1, ct);
            await AssertHealthAsync(http, bridge, HttpStatusCode.OK, "Healthy", ct);
            await AssertHealthAsync(http, transformer, HttpStatusCode.OK, "Healthy", ct);
            var disk = bridge.Services.GetRequiredService<SqliteBuffer>();
            // Keep a test-only entry pending on an absent topic while Kafka remains reachable.
            Assert.True(disk.TryAppend(new BufferedEntry("test.pending", "Pump01", [1], 0, DateTimeOffset.UtcNow)));
            await AssertHealthAsync(http, bridge, HttpStatusCode.OK, "Degraded", ct);
            disk.DeleteAcknowledged(disk.ReadOldest()!);
            await AssertHealthAsync(http, bridge, HttpStatusCode.OK, "Healthy", ct);
            await mqtt.Broker.StopAsync(ct);
            while (bridge.Services.GetRequiredService<Telemetrix.Edge.Bridge.Sources.ISourceAdapter>().IsConnected)
                await Task.Delay(50, ct);
            await AssertHealthAsync(http, bridge, HttpStatusCode.ServiceUnavailable, "Unhealthy", ct, "source");
            await environment.Kafka.StopAsync(ct);
            await AssertHealthAsync(http, bridge, HttpStatusCode.ServiceUnavailable, "Unhealthy", ct, "kafka");
            await AssertHealthAsync(http, transformer, HttpStatusCode.ServiceUnavailable, "Unhealthy", ct, "kafka");
            await environment.Database.StopAsync(ct);
            await AssertHealthAsync(http, transformer, HttpStatusCode.ServiceUnavailable, "Unhealthy", ct, "database");
            await bridge.StopAsync(ct);
            await transformer.StopAsync(ct);
        }
        finally
        {
            foreach (var path in new[] { buffer, buffer + "-wal", buffer + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    internal static string Address(WebApplication app) => app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);

    internal static async Task AssertHealthAsync(HttpClient http, WebApplication app, HttpStatusCode code,
        string status, CancellationToken ct, string? failedCheck = null)
    {
        using var response = await http.GetAsync(Address(app) + "/health", ct);
        Assert.Equal(code, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(status, json.RootElement.GetProperty("status").GetString());
        if (failedCheck is not null)
            Assert.Equal("Unhealthy", json.RootElement.GetProperty("checks").GetProperty(failedCheck).GetProperty("status").GetString());
    }
}
