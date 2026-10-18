using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Telemetrix.Transformer.Tests;

public class TransformerOptionsTests
{
    [Theory]
    [InlineData("TagResolution:UnknownTagPolicy", "automatic")]
    [InlineData("Kafka:BootstrapServers", "")]
    public async Task InvalidOptionsFailBeforeTopicStartup(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        builder.Services.AddTransformerMetadata(builder.Configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }
}
