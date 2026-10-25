using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Batching;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Edge.Bridge.Sources.Mqtt;

namespace Telemetrix.Edge.Bridge;

public static class BridgeServiceCollectionExtensions
{
    public static IServiceCollection AddBridgePipeline(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<BatchingOptions>().Bind(configuration.GetSection(BatchingOptions.SectionName))
            .Validate(options => options.MaxSamples > 0, "Batching:MaxSamples must be positive.")
            .Validate(options => options.MaxAge > TimeSpan.Zero, "Batching:MaxAge must be positive.")
            .ValidateOnStart();
        services.AddOptions<KafkaOptions>().Bind(configuration.GetSection(KafkaOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers),
                "Kafka:BootstrapServers is required.")
            .ValidateOnStart();
        services.AddOptions<BufferOptions>().Bind(configuration.GetSection(BufferOptions.SectionName))
            .Validate(options => options.MaxBytes > 0, "Buffer:MaxBytes must be positive.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Path), "Buffer:Path is required.")
            .ValidateOnStart();
        services.AddSingleton<SqliteBuffer>();
        services.AddSingleton<IKafkaTransport, KafkaTransport>();
        services.AddSingleton<StoreAndForwardQueue>();
        services.AddSingleton<IPlantEventPublisher, PlantEventPublisher>();
        services.AddSingleton<PlantEventTracker>();
        var mqtt = string.Equals(configuration["Source:Kind"], "Mqtt", StringComparison.OrdinalIgnoreCase);
        services.AddSingleton(provider => new AssetBatcher(
            provider.GetRequiredService<IOptions<BatchingOptions>>().Value,
            provider.GetRequiredService<TimeProvider>(), mqtt ? "mqtt" : "opcua",
            mqtt ? provider.GetRequiredService<IOptions<MqttOptions>>().Value.Endpoint
                : provider.GetRequiredService<IOptions<OpcUaOptions>>().Value.Endpoint));
        services.AddSingleton<IBatchPublisher, KafkaBatchPublisher>();
        // Hosted services start in registration order: topic readiness precedes source ingestion.
        services.AddHostedService<TopicInitializer>();
        services.AddHostedService<BufferReplayWorker>();
        services.AddHostedService<OpcUaIngestionWorker>();
        return services;
    }
}
