using Npgsql;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer;

public static class TransformerServiceCollectionExtensions
{
    public static IServiceCollection AddTransformerMetadata(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<KafkaOptions>().Bind(configuration.GetSection(KafkaOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers),
                "Kafka:BootstrapServers is required.").ValidateOnStart();
        services.AddOptions<TagResolutionOptions>().Bind(configuration.GetSection(TagResolutionOptions.SectionName))
            .Validate(options => options.UnknownTagPolicy is TagResolutionOptions.DeadLetter or TagResolutionOptions.Create,
                "TagResolution:UnknownTagPolicy must be dead-letter or create.").ValidateOnStart();
        services.AddSingleton(_ => NpgsqlDataSource.Create(
            configuration.GetConnectionString("Telemetry")
            ?? throw new InvalidOperationException("ConnectionStrings:Telemetry is required.")));
        services.AddSingleton<ITagCatalog, PostgresTagCatalog>();
        services.AddSingleton<TagResolver>();
        services.AddSingleton<IDeadLetterPublisher, KafkaDeadLetterPublisher>();
        services.AddHostedService<DeadLetterTopicInitializer>();
        return services;
    }
}
