using Npgsql;
using Polly;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
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
        services.AddSingleton<IRawBatchHandler, TelemetryBatchHandler>();
        services.AddSingleton<RawBatchProcessor>();
        services.AddHostedService<TagCatalogInitializer>();
        services.AddHostedService<RawBatchConsumer>();
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
        services.AddSingleton<KafkaDeadLetterPublisher>();
        services.AddSingleton<IDeadLetterPublisher>(provider => provider.GetRequiredService<KafkaDeadLetterPublisher>());
        services.AddSingleton<IInvalidBatchPublisher>(provider => provider.GetRequiredService<KafkaDeadLetterPublisher>());
        services.AddHostedService<DeadLetterTopicInitializer>();
        return services;
    }

    private static bool IsValidConnectionString(string value)
    {
        try { _ = new NpgsqlConnectionStringBuilder(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
