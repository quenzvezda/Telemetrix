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
                .Validate(o => ValidFilter(o.TopicFilter), "Source:Mqtt:TopicFilter must match plant/{plant}/{line}/{asset}/{tag}; '+' is allowed per identity segment.")
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
        var parts = filter.Split('/');
        return parts.Length == 5 && parts[0] == "plant" && parts.Skip(1).All(p =>
            !string.IsNullOrWhiteSpace(p) && (p == "+" || p.IndexOfAny(['+', '#', '\0']) < 0));
    }
}
