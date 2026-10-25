using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telemetrix.MqttSim;

await using var app = MqttSimApplication.Build(args);
await app.RunAsync();
