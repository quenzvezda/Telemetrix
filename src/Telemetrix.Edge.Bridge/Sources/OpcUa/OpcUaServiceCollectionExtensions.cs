using Microsoft.Extensions.Options;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

public static class OpcUaServiceCollectionExtensions
{
    public static IServiceCollection AddOpcUaOptions(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<OpcUaOptions>, OpcUaOptionsValidator>();
        services.AddOptions<OpcUaOptions>()
            .Bind(configuration.GetSection(OpcUaOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}
