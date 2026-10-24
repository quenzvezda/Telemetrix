using Telemetrix.PlantSim;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPlantSimulator(builder.Configuration);

var host = builder.Build();
host.Run();
