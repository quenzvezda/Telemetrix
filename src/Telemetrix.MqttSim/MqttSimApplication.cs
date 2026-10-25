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
        return builder.Build();
    }
}
