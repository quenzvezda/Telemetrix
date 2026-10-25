using Telemetrix.PlantSim;

await using var app = PlantSimApplication.Build(args);
await app.RunAsync();
