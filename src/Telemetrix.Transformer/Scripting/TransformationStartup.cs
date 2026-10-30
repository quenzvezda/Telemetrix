namespace Telemetrix.Transformer.Scripting;

// Fails startup before raw consumption when the schema, registry or a script is not usable.
public sealed class TransformationStartup(ITransformationRunStore runs, TransformationRegistry registry,
    PythonWorker worker, ILogger<TransformationStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await runs.SchemaReadyAsync(cancellationToken))
            throw new InvalidOperationException(
                "Transformations are enabled but deploy/sql/migrations/003_transformations.sql has not been applied.");
        await worker.StartAsync(cancellationToken);
        logger.LogInformation("Transformations ready: {Labels}",
            string.Join(", ", registry.All.Select(entry => $"{entry.Label}={entry.Version}")));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
