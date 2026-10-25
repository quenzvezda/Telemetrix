using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System.Net;
using System.Net.Sockets;

namespace Telemetrix.Integration.Tests;

internal sealed class MqttFixture : IAsyncDisposable
{
    public MqttFixture(INetwork? network = null)
    {
        // Keep the advertised host port stable across StopAsync/StartAsync on Docker Desktop.
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var builder = new ContainerBuilder("eclipse-mosquitto:2.1.2-alpine")
        .WithPortBinding(port, 1883)
        .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes("listener 1883\nallow_anonymous true\npersistence false\n"),
            "/mosquitto/config/mosquitto.conf")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"));
        if (network is not null) builder = builder.WithNetwork(network).WithNetworkAliases("mqtt");
        Broker = builder.Build();
    }

    public IContainer Broker { get; }

    public string Host => Broker.Hostname;
    public int Port => Broker.GetMappedPublicPort(1883);

    public async Task<IMqttClient> ConnectAsync(CancellationToken ct, string? clientId = null)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(Host, Port)
                .WithClientId(clientId ?? Guid.NewGuid().ToString("N"))
                .WithProtocolVersion(MqttProtocolVersion.V311).Build(), ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public static async Task PublishAsync(IMqttClient client, DateTimeOffset timestamp, CancellationToken ct,
        string value = "52.8", string tag = "Pump01/FlowRate", bool retain = false)
    {
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"plant/Plant01/Line01/{tag}")
            .WithPayload($$"""{"value":{{value}},"quality":"good","sourceTimestamp":"{{timestamp:O}}"}""")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(retain).Build(), ct);
    }

    public async ValueTask DisposeAsync() => await Broker.DisposeAsync();
}
