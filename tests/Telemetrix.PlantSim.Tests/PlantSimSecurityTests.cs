using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Telemetrix.PlantSim.Tests;

public class PlantSimSecurityTests
{
    [Theory]
    [InlineData("Production", "SignAndEncrypt", true)]
    [InlineData("Production", "None", false)]
    [InlineData("Staging", "None", false)]
    [InlineData("Development", "None", true)]
    [InlineData("Development", "Invalid", false)]
    public void ServerRejectsInsecureConfigurationOutsideDevelopment(string environment, string mode, bool valid)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["PlantSim:SecurityMode"] = mode });
        builder.Services.AddPlantSimulator(builder.Configuration);
        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<PlantSimOptions>>();
        if (valid) Assert.Equal(mode, options.Value.SecurityMode);
        else Assert.Contains("PlantSim:SecurityMode", Assert.Throws<OptionsValidationException>(() => options.Value).Message);
    }
}
