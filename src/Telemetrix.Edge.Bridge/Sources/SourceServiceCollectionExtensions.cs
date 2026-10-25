using Opc.Ua;
using Telemetrix.Edge.Bridge.Sources.Mqtt;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Edge.Bridge.Sources;

public static class SourceServiceCollectionExtensions
{
    public static IServiceCollection AddBridgeSource(this IServiceCollection services, IConfiguration configuration)
    {
        var kind = configuration["Source:Kind"] ?? "OpcUa";
        if (kind.Equals("Mqtt", StringComparison.OrdinalIgnoreCase))
        {
            services.AddOptions<MqttOptions>().Bind(configuration.GetSection("Source:Mqtt"))
                .Validate(o => !string.IsNullOrWhiteSpace(o.Host) && o.Port is > 0 and <= 65535,
                    "Source:Mqtt requires a host and valid port.")
                .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId), "Source:Mqtt:ClientId is required.")
                .Validate(o => ValidFilter(o.TopicFilter), "Source:Mqtt:TopicFilter must be a valid MQTT topic filter.")
                .ValidateOnStart();
            services.AddSingleton<ISourceAdapter, MqttSourceAdapter>();
        }
        else if (kind.Equals("OpcUa", StringComparison.OrdinalIgnoreCase))
        {
            services.AddOpcUaOptions(configuration);
            services.AddSingleton<ITelemetryContext, ServiceProviderTelemetryContext>();
            services.AddSingleton<ISourceAdapter, OpcUaSourceAdapter>();
        }
        else throw new InvalidOperationException("Source:Kind must be OpcUa or Mqtt.");
        return services;
    }

    private static bool ValidFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(filter) > ushort.MaxValue)
            return false;
        var parts = filter.Split('/');
        return parts.Select((part, i) => part == "+" || (part == "#" && i == parts.Length - 1)
            || part.IndexOfAny(['+', '#']) < 0).All(valid => valid);
    }
}
