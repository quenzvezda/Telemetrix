using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telemetrix.MqttSim;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMqttSimulator(builder.Configuration);
await builder.Build().RunAsync();
