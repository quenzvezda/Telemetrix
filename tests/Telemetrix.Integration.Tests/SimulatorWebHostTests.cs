using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using Opc.Ua.Client;
using Telemetrix.MqttSim;
using Telemetrix.PlantSim;

namespace Telemetrix.Integration.Tests;

public class SimulatorWebHostTests
{
    [Fact]
    public async Task SimulatorHttpListenersKeepSevenTagsAvailable()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(1));
        var ct = deadline.Token;
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var pki = Path.Combine(Path.GetTempPath(), $"telemetrix-web-pki-{Guid.NewGuid():N}");
        await using var mqtt = new MqttFixture();
        await mqtt.Broker.StartAsync(ct);
        using var subscriber = await mqtt.ConnectAsync(ct);
        var topics = new ConcurrentDictionary<string, int>();
        subscriber.ApplicationMessageReceivedAsync += e =>
        {
            topics.AddOrUpdate(e.ApplicationMessage.Topic, 1, (_, count) => count + 1);
            return Task.CompletedTask;
        };
        await subscriber.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("plant/#").Build(), ct);
        try
        {
            await using var plant = PlantSimApplication.Build(["--Http:Port=0", $"--PlantSim:Endpoint=opc.tcp://localhost:{port}",
                $"--PlantSim:PkiPath={pki}", "--Faults:Random:Enabled=false"]);
            await using var simulator = MqttSimApplication.Build(["--Http:Port=0", $"--MqttSim:Host={mqtt.Host}",
                $"--MqttSim:Port={mqtt.Port}"]);
            await plant.StartAsync(ct);
            await plant.Services.GetRequiredService<Worker>().Ready.Task.WaitAsync(ct);
            await simulator.StartAsync(ct);
            using var http = new HttpClient();
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(WebHostTests.Address(plant), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(WebHostTests.Address(simulator), ct)).StatusCode);
            while (topics.Count != 7 || topics.Values.Any(count => count < 2)) await Task.Delay(50, ct);
            await WebHostTests.AssertHealthAsync(http, plant, HttpStatusCode.OK, "Healthy", ct);
            await WebHostTests.AssertHealthAsync(http, simulator, HttpStatusCode.OK, "Healthy", ct);
            await mqtt.Broker.StopAsync(ct);
            while (simulator.Services.GetRequiredService<MqttSimulator>().IsReady) await Task.Delay(50, ct);
            await WebHostTests.AssertHealthAsync(http, simulator, HttpStatusCode.ServiceUnavailable, "Unhealthy", ct, "broker");
            var before = topics.Values.Sum();
            await mqtt.Broker.StartAsync(ct);
            await subscriber.ReconnectAsync(cancellationToken: ct);
            await subscriber.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("plant/#").Build(), ct);
            while (!simulator.Services.GetRequiredService<MqttSimulator>().IsReady || topics.Values.Sum() <= before)
                await Task.Delay(50, ct);
            await WebHostTests.AssertHealthAsync(http, simulator, HttpStatusCode.OK, "Healthy", ct);
            // A real OPC UA client verifies the server independently of the new HTTP listener.
            await using var client = new PlantSimFixture($"opc.tcp://localhost:{port}");
            using var session = await client.ConnectAsync(ct);
            foreach (var tag in Telemetrix.Simulation.PlantTags.All)
            {
                var value = await session.ReadValueAsync(new Opc.Ua.NodeId(tag.Identifier, 2), ct);
                Assert.True(Opc.Ua.StatusCode.IsGood(value.StatusCode));
            }
            await simulator.StopAsync(ct);
            await plant.StopAsync(ct);
            Assert.False(plant.Services.GetRequiredService<Worker>().IsReady);
            Assert.False(simulator.Services.GetRequiredService<MqttSimulator>().IsReady);
        }
        finally { if (Directory.Exists(pki)) Directory.Delete(pki, true); }
    }
}
