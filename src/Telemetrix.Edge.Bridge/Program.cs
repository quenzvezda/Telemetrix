using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Sources;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddBridgeSource(builder.Configuration);
builder.Services.AddBridgePipeline(builder.Configuration);

var host = builder.Build();
host.Run();
