using System.Net;
using Microsoft.AspNetCore.Builder;
using Telemetrix.Edge.Bridge;
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
}
