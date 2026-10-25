using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.DependencyInjection;
using Opc.Ua;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Integration.Tests;

public class OpcUaSecurityTests
{
    [Fact]
    public async Task EndpointSelectionNeverFallsBackToNone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new ContainerBuilder("mcr.microsoft.com/iotedge/opc-plc:2.15.11")
            .WithPortBinding(50000, true)
            .WithCommand("--pn=50000", "--autoaccept", "--ut")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("OPC UA Server started"))
            .Build();
        await server.StartAsync(ct);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var telemetry = new ServiceProviderTelemetryContext(services);
        var configuration = new ApplicationConfiguration(telemetry);
        var options = new OpcUaOptions { Endpoint = $"opc.tcp://{server.Hostname}:{server.GetMappedPublicPort(50000)}" };
        // OPC PLC also advertises secure endpoints by default. First discover its real
        // None endpoint, then exercise the exact mode/policy filter with a None-only server.
        options.Security.Mode = "None";
        Assert.Equal(MessageSecurityMode.None,
            (await OpcUaEndpointSelector.SelectAsync(configuration, options, telemetry, ct)).SecurityMode);
        await using var noneOnly = new PlantSimFixture();
        await noneOnly.StartAsync(ct);
        options.Endpoint = noneOnly.Endpoint;
        options.Security.Mode = "SignAndEncrypt";
        var error = await Assert.ThrowsAsync<ServiceResultException>(() =>
            OpcUaEndpointSelector.SelectAsync(configuration, options, telemetry, ct));
        Assert.Contains("No matching endpoint", error.Message);
        Assert.Equal(StatusCodes.BadSecurityPolicyRejected, error.StatusCode);
    }
}
