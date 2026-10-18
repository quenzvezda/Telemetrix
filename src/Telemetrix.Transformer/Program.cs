using Telemetrix.Transformer;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddTransformerMetadata(builder.Configuration);

var host = builder.Build();
host.Run();
