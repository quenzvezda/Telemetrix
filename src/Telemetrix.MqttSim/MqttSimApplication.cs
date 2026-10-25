namespace Telemetrix.MqttSim;

public static class MqttSimApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => { options.IncludeScopes = true; options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue("Http:Port", 5104)}");
        builder.Services.AddMqttSimulator(builder.Configuration);
        var app = builder.Build();
        app.MapGet("/health", () =>
        {
            var ready = app.Services.GetRequiredService<MqttSimulator>().IsReady;
            return Results.Json(new { status = ready ? "Healthy" : "Unhealthy", checks = new
                { broker = new { status = ready ? "Healthy" : "Unhealthy", description = ready ? null : "MQTT broker or publishing loop is not ready." } } },
                statusCode: ready ? 200 : 503);
        });
        return app;
    }
}
