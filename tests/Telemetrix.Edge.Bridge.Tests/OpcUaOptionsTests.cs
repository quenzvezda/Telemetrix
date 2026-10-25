using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Edge.Bridge.Tests;

public class OpcUaOptionsTests
{
    [Fact]
    public async Task DevelopmentConfigurationStartsWithSevenMappedTags()
    {
        using var host = CreateBuilder().Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var options = host.Services.GetRequiredService<IOptions<OpcUaOptions>>().Value;

        Assert.Equal("Plant01", options.Plant);
        Assert.Equal("Line01", options.Line);
        Assert.Equal("opc.tcp://localhost:50000", options.Endpoint);
        Assert.Equal("None", options.Security.Mode);
        Assert.Equal(500, options.SamplingIntervalMs);
        Assert.Equal(1000, options.PublishingIntervalMs);
        Assert.Equal(10u, options.QueueSize);
        Assert.Equal(7, options.Tags.Count);
        Assert.Equal(7, options.Tags.Select(tag => (tag.Asset, tag.Tag)).Distinct().Count());
        Assert.Equal("ns=3;s=AlternatingBoolean",
            Assert.Single(options.Tags, tag => tag.Tag == "Running").NodeId);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("OpcUa:Tags:0:NodeId", "")]
    [InlineData("OpcUa:Tags:0:Asset", " ")]
    [InlineData("OpcUa:Tags:0:Tag", "")]
    [InlineData("OpcUa:Plant", "")]
    [InlineData("OpcUa:Line", "")]
    [InlineData("OpcUa:Endpoint", "https://localhost:50000")]
    [InlineData("OpcUa:Endpoint", "localhost:50000")]
    [InlineData("OpcUa:Endpoint", "")]
    [InlineData("OpcUa:SamplingIntervalMs", "0")]
    [InlineData("OpcUa:PublishingIntervalMs", "-1")]
    [InlineData("OpcUa:QueueSize", "0")]
    [InlineData("OpcUa:Security:Mode", "Invalid")]
    [InlineData("OpcUa:Security:Policy", "None")]
    [InlineData("OpcUa:Security:PkiRoot", " ")]
    public async Task InvalidConfigurationFailsAtHostStartupAndNamesTheSetting(string key, string value)
    {
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(key, exception.Message);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("ns=3;s=Telemetry", true)]
    public async Task EmptyTagListRequiresABrowseStartNode(string? browseStartNode, bool canStart)
    {
        var builder = CreateBuilder(includeDevelopment: false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpcUa:BrowseStartNode"] = browseStartNode
        });
        using var host = builder.Build();

        if (canStart)
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains("OpcUa:Tags", exception.Message);
            Assert.Contains("OpcUa:BrowseStartNode", exception.Message);
        }
    }

    [Fact]
    public async Task EnvironmentVariableOverridesTheJsonEndpoint()
    {
        var prefix = $"TELEMETRIX_TEST_{Guid.NewGuid():N}_";
        var variable = $"{prefix}OpcUa__Endpoint";
        Environment.SetEnvironmentVariable(variable, "opc.tcp://opc-plc:50000");
        try
        {
            var builder = CreateBuilder();
            builder.Configuration.AddEnvironmentVariables(prefix);
            using var host = builder.Build();

            await host.StartAsync(TestContext.Current.CancellationToken);
            var options = host.Services.GetRequiredService<IOptions<OpcUaOptions>>().Value;

            Assert.Equal("opc.tcp://opc-plc:50000", options.Endpoint);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static HostApplicationBuilder CreateBuilder(bool includeDevelopment = true)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
        if (includeDevelopment)
        {
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.Development.json"));
        }

        builder.Services.AddOpcUaOptions(builder.Configuration);
        return builder;
    }

    [Theory]
    [InlineData("Production", "None", false, false)]
    [InlineData("Staging", "None", false, false)]
    [InlineData("PlantSim", "None", false, false)]
    [InlineData("Production", "SignAndEncrypt", true, false)]
    [InlineData("Production", "SignAndEncrypt", false, true)]
    [InlineData("Development", "None", false, true)]
    public async Task SecuritySettingsAreValidatedAtStartup(string environment, string mode, bool autoAccept, bool starts)
    {
        var builder = CreateBuilder();
        builder.Environment.EnvironmentName = environment;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpcUa:Security:Mode"] = mode,
            ["OpcUa:Security:AutoAcceptUntrusted"] = autoAccept.ToString()
        });
        using var host = builder.Build();
        if (starts)
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains("OpcUa:Security:", error.Message);
        }
    }
}
