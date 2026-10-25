using Telemetrix.Edge.Bridge.Sources;

namespace Telemetrix.Edge.Bridge;

public static class BridgeApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue("Http:Port", 5101)}");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddBridgeSource(builder.Configuration);
        builder.Services.AddBridgePipeline(builder.Configuration);
        return builder.Build();
    }
}
