using Telemetrix.Edge.Bridge;
await using var app = BridgeApplication.Build(args);
await app.RunAsync();
