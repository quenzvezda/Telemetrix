using Npgsql;
using Polly;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer;

public static class TransformerServiceCollectionExtensions
{
    public static IServiceCollection AddTransformerPipeline(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddTransformerMetadata(configuration);
        services.AddSingleton<IMeasurementWriter, MeasurementWriter>();
        services.AddSingleton<ResiliencePipeline>(provider => StorageRetry.Create(provider.GetRequiredService<TimeProvider>()));
        services.AddTransformations(configuration);
        services.AddSingleton<IRawBatchHandler, TelemetryBatchHandler>();
        services.AddSingleton<RawBatchProcessor>();
        services.AddSingleton<ConsumerAssignments>();
        services.AddHostedService<ConsumerLagSampler>();
        services.AddHostedService<TagCatalogInitializer>();
        // Registered after the other initializers and before consumption, so a broken script stops startup.
        if (TransformationsEnabled(configuration)) services.AddHostedService<TransformationStartup>();
        services.AddHostedService<RawBatchConsumer>();
        if (configuration.GetValue<bool>("PlantEvents:Enabled"))
        {
            services.AddSingleton<PlantEventWriter>();
            services.AddHostedService<PlantEventConsumer>();
        }
        return services;
    }

    public static IServiceCollection AddTransformerMetadata(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<KafkaOptions>().Bind(configuration.GetSection(KafkaOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers),
                "Kafka:BootstrapServers is required.").ValidateOnStart();
        services.AddOptions<TagResolutionOptions>().Bind(configuration.GetSection(TagResolutionOptions.SectionName))
            .Validate(options => options.UnknownTagPolicy is TagResolutionOptions.DeadLetter or TagResolutionOptions.Create,
                "TagResolution:UnknownTagPolicy must be dead-letter or create.").ValidateOnStart();
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Telemetry),
                "ConnectionStrings:Telemetry is required.")
            .Validate(options => IsValidConnectionString(options.Telemetry),
                "ConnectionStrings:Telemetry must be a valid PostgreSQL connection string.").ValidateOnStart();
        services.AddSingleton(provider => NpgsqlDataSource.Create(
            provider.GetRequiredService<IOptions<StorageOptions>>().Value.Telemetry));
        services.AddSingleton<ITagCatalog, PostgresTagCatalog>();
        services.AddSingleton<TagResolver>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<TransformerMetrics>();
        services.AddSingleton<KafkaDeadLetterPublisher>();
        services.AddSingleton<IDeadLetterPublisher>(provider => provider.GetRequiredService<KafkaDeadLetterPublisher>());
        services.AddSingleton<IInvalidBatchPublisher>(provider => provider.GetRequiredService<KafkaDeadLetterPublisher>());
        services.AddHostedService<DeadLetterTopicInitializer>();
        return services;
    }

    private static IServiceCollection AddTransformations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TransformationOptions>().Bind(configuration.GetSection(TransformationOptions.SectionName))
            .Validate(options => options.TimeoutMs > 0, "Transformations:TimeoutMs must be positive.")
            .Validate(options => options.MaxOutputSamples > 0, "Transformations:MaxOutputSamples must be positive.")
            .Validate(options => options.MemoryLimitMb >= 0, "Transformations:MemoryLimitMb must not be negative.")
            .Validate(options => !options.Enabled || (!string.IsNullOrWhiteSpace(options.Directory)
                && !string.IsNullOrWhiteSpace(options.PythonExecutable)),
                "Transformations:Directory and Transformations:PythonExecutable are required when enabled.")
            .ValidateOnStart();
        services.AddSingleton<TransformationStage>();
        if (!TransformationsEnabled(configuration)) return services;
        services.AddSingleton(provider => TransformationRegistry.Load(
            provider.GetRequiredService<IOptions<TransformationOptions>>().Value.Directory));
        services.AddSingleton<PythonWorker>();
        services.AddSingleton<ITransformationRuntime>(provider => provider.GetRequiredService<PythonWorker>());
        services.AddSingleton<IDerivedTagCatalog, PostgresDerivedTagCatalog>();
        services.AddSingleton<ITransformationRunStore, TransformationRunStore>();
        services.AddSingleton<TransformationEngine>();
        return services;
    }

    private static bool TransformationsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>($"{TransformationOptions.SectionName}:Enabled");

    private static bool IsValidConnectionString(string value)
    {
        try { _ = new NpgsqlConnectionStringBuilder(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
