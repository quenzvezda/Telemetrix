using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Edge.Bridge.Tests;

public class BridgePipelineOptionsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"telemetrix-options-{Guid.NewGuid():N}");
    [Theory]
    [InlineData("Batching:MaxSamples", "0")]
    [InlineData("Batching:MaxAge", "00:00:00")]
    [InlineData("Kafka:BootstrapServers", "")]
    [InlineData("Buffer:MaxBytes", "0")]
    [InlineData("Buffer:Path", "")]
    public async Task InvalidPipelineOptionsFailBeforeStartingKafka(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Buffer:Path"] = Path.Combine(directory, "bridge.db")
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<OpcUaOptions>(options => options.Endpoint = "opc.tcp://localhost:50000");
        builder.Services.AddSingleton<ISourceAdapter, UnusedSource>();
        builder.Services.AddBridgePipeline(builder.Configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    private sealed class UnusedSource : ISourceAdapter
    {
        public Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid configuration must prevent ingestion.");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
