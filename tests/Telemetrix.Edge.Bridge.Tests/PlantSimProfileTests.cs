using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Edge.Bridge.Tests;

public sealed class PlantSimProfileTests
{
    [Fact]
    public async Task ProfileLoadsSevenPlantSimTagsWithoutChangingTheBridge()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.PlantSim.json"));
        builder.Services.AddOpcUaOptions(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var options = host.Services.GetRequiredService<IOptions<OpcUaOptions>>().Value;
        Assert.Equal("opc.tcp://localhost:4840", options.Endpoint);
        Assert.Equal(7, options.Tags.Count);
        Assert.Equal(5, options.Tags.Count(tag => tag.Asset == "Pump01"));
        Assert.Equal(2, options.Tags.Count(tag => tag.Asset == "Tank01"));
        Assert.All(options.Tags, tag => Assert.Equal($"ns=2;s=Line01.{tag.Asset}.{tag.Tag}", tag.NodeId));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
