using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Sources.Mqtt;
using Telemetrix.Edge.Bridge.Publishing;

namespace Telemetrix.Edge.Bridge.Tests;

public sealed class MqttSourceOptionsTests
{
    [Fact]
    public async Task MqttSourceStartsWithoutOpcUaSettingsAndAcceptsEnvironmentOverride()
    {
        var prefix = $"TELEMETRIX_MQTT_TEST_{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Source__Kind", "Mqtt");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddEnvironmentVariables(prefix);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IPlantEventPublisher, NoEvents>();
            builder.Services.AddBridgeSource(builder.Configuration);
            using var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            Assert.IsType<MqttSourceAdapter>(host.Services.GetRequiredService<ISourceAdapter>());
            Assert.Equal("mqtt://localhost:1883", host.Services.GetRequiredService<IOptions<MqttOptions>>().Value.Endpoint);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally { Environment.SetEnvironmentVariable(prefix + "Source__Kind", null); }
    }

    [Theory]
    [InlineData("Source:Mqtt:Port", "0")]
    [InlineData("Source:Mqtt:ClientId", "")]
    [InlineData("Source:Mqtt:TopicFilter", "plant/#/invalid")]
    public async Task InvalidMqttSettingsFailAtStartup(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Source:Kind"] = "Mqtt", [key] = value });
        builder.Services.AddBridgeSource(builder.Configuration);
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }

    private sealed class NoEvents : IPlantEventPublisher
    {
        public Task PublishAsync(Telemetrix.Contracts.PlantEvent plantEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
