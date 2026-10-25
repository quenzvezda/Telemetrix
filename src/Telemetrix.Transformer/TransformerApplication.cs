using Telemetrix.Transformer.Health;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer;

public static class TransformerApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => { options.IncludeScopes = true; options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue("Http:Port", 5102)}");
        builder.Services.AddTransformerPipeline(builder.Configuration);
        var prometheus = builder.Configuration.GetValue<bool>("Telemetry:Prometheus:Enabled");
        builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("Telemetrix.Transformer"))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(TransformerMetrics.MeterName);
                if (builder.Configuration.GetValue("Telemetry:Console:Enabled", builder.Environment.IsDevelopment())) metrics.AddConsoleExporter();
                if (prometheus) metrics.AddPrometheusExporter(options =>
                {
                    options.ResourceConstantLabels = key => key == "service.name";
                    options.ScrapeResponseCacheDurationMilliseconds = 0;
                });
            });
        builder.Services.AddHealthChecks().AddCheck<KafkaHealthCheck>("kafka").AddCheck<DatabaseHealthCheck>("database");
        var app = builder.Build();
        if (prometheus) app.MapPrometheusScrapingEndpoint();
        app.MapHealthChecks("/health", new()
        {
            ResponseWriter = (http, report) => http.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.ToDictionary(entry => entry.Key,
                    entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description })
            })
        });
        return app;
    }
}
