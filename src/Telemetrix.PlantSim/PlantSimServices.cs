using Opc.Ua;
using Telemetrix.PlantSim.Model;
using Telemetrix.PlantSim.Faults;

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
        services.AddOptions<RandomFaultOptions>().Bind(configuration.GetSection("Faults:Random"))
            .Validate(options => new[] { options.PumpTripsPerHour, options.VibrationSpikesPerHour, options.BadQualityPerHour }
                .All(rate => double.IsFinite(rate) && rate is >= 0 and <= 3600),
                "Faults:Random rates must be finite and between 0 and 3600 per hour.")
            .Validate(options => options.PumpRestartDelay > TimeSpan.Zero &&
                options.PumpRestartDelay <= TimeSpan.FromDays(1), "Faults:Random:PumpRestartDelay must be within one day.")
            .Validate(options => options.BadQualityDuration > TimeSpan.Zero &&
                options.BadQualityDuration <= TimeSpan.FromDays(1), "Faults:Random:BadQualityDuration must be within one day.")
            .ValidateOnStart();
        services.AddHostedService<RandomFaultScheduler>();
        return services;
    }
}
