namespace Telemetrix.Transformer.Pipeline;

public sealed class TagCatalogInitializer(TagResolver resolver, ILogger<TagCatalogInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await resolver.InitializeAsync(cancellationToken);
        logger.LogInformation("Loaded tag catalog before starting raw consumption");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
