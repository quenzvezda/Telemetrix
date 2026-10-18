using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOpcUaOptions(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
