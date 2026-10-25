using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Health;
using Microsoft.AspNetCore.Http;

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
        builder.Services.AddHealthChecks().AddCheck<SourceHealthCheck>("source")
            .AddCheck<KafkaHealthCheck>("kafka").AddCheck<BufferHealthCheck>("buffer");
        var app = builder.Build();
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
