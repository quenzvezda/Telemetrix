using Telemetrix.Transformer;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddTransformerPipeline(builder.Configuration);

var host = builder.Build();
host.Run();
