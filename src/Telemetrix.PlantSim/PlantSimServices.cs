using Opc.Ua;
using Telemetrix.PlantSim.Model;

namespace Telemetrix.PlantSim;

public static class PlantSimServices
{
    public static IServiceCollection AddPlantSimulator(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PlantSimOptions>().Bind(configuration.GetSection("PlantSim"))
            .Validate(options => Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) &&
                uri.Scheme == "opc.tcp" && uri.Port > 0, "PlantSim:Endpoint must be an opc.tcp URL with a port.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.PkiPath), "PlantSim:PkiPath is required.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITelemetryContext, ServiceProviderTelemetryContext>();
        services.AddSingleton<PumpingLine>();
        services.AddSingleton<Worker>();
        services.AddHostedService(provider => provider.GetRequiredService<Worker>());
        return services;
    }
}
