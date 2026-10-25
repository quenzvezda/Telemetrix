using Telemetrix.Transformer.Health;

namespace Telemetrix.Transformer;

public static class TransformerApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue("Http:Port", 5102)}");
        builder.Services.AddTransformerPipeline(builder.Configuration);
        builder.Services.AddHealthChecks().AddCheck<KafkaHealthCheck>("kafka").AddCheck<DatabaseHealthCheck>("database");
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
