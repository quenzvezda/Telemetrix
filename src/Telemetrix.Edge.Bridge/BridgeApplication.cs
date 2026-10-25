using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Health;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Telemetrix.Edge.Bridge.Batching;

namespace Telemetrix.Edge.Bridge;

public static class BridgeApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => { options.IncludeScopes = true; options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue("Http:Port", 5101)}");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddBridgeSource(builder.Configuration);
        builder.Services.AddBridgePipeline(builder.Configuration);
        var prometheus = builder.Configuration.GetValue<bool>("Telemetry:Prometheus:Enabled");
        builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("Telemetrix.Bridge"))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(BridgeMetrics.MeterName);
                if (builder.Configuration.GetValue("Telemetry:Console:Enabled", builder.Environment.IsDevelopment())) metrics.AddConsoleExporter();
                if (prometheus) metrics.AddPrometheusExporter(options =>
                {
                    options.ResourceConstantLabels = key => key == "service.name";
                    options.ScrapeResponseCacheDurationMilliseconds = 0;
                });
            });
        builder.Services.AddHealthChecks().AddCheck<SourceHealthCheck>("source")
            .AddCheck<KafkaHealthCheck>("kafka").AddCheck<BufferHealthCheck>("buffer");
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
