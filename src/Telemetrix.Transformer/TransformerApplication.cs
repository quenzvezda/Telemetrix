namespace Telemetrix.Transformer;

public static class TransformerApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue("Http:Port", 5102)}");
        builder.Services.AddTransformerPipeline(builder.Configuration);
        return builder.Build();
    }
}
