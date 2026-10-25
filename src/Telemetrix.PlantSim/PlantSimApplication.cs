namespace Telemetrix.PlantSim;

public static class PlantSimApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue("Http:Port", 5103)}");
        builder.Services.AddPlantSimulator(builder.Configuration);
        return builder.Build();
    }
}
