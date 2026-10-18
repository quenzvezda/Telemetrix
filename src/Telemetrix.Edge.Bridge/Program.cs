using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using Opc.Ua;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOpcUaOptions(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITelemetryContext, ServiceProviderTelemetryContext>();
builder.Services.AddSingleton<ISourceAdapter, OpcUaSourceAdapter>();
builder.Services.AddHostedService<OpcUaIngestionWorker>();

var host = builder.Build();
host.Run();
