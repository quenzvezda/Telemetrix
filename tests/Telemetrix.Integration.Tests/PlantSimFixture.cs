using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Telemetrix.PlantSim;

namespace Telemetrix.Integration.Tests;

internal sealed class PlantSimFixture : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"telemetrix-plantsim-{Guid.NewGuid():N}");
    private readonly IHost host;
    public string Endpoint { get; }
    public ITelemetryContext Telemetry => host.Services.GetRequiredService<ITelemetryContext>();
    public Worker Worker => host.Services.GetRequiredService<Worker>();

    public PlantSimFixture()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Endpoint = $"opc.tcp://localhost:{port}";
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlantSim:Endpoint"] = Endpoint,
            ["PlantSim:PkiPath"] = directory,
            ["Faults:Random:Enabled"] = "false"
        });
        builder.Services.AddPlantSimulator(builder.Configuration);
        host = builder.Build();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await host.StartAsync(ct);
        await Worker.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
    }

    public async Task<ISession> ConnectAsync(CancellationToken ct)
    {
        var configuration = new ApplicationConfiguration(Telemetry)
        {
            ApplicationName = "Telemetrix.PlantSim.Integration",
            ApplicationUri = "urn:telemetrix:plantsim:test",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificates =
                [
                    new CertificateIdentifier
                    {
                        StoreType = "Directory", StorePath = Path.Combine(directory, "client"),
                        SubjectName = "CN=Telemetrix.PlantSim.Integration"
                    }
                ],
                TrustedPeerCertificates = new CertificateTrustList
                { StoreType = "Directory", StorePath = Path.Combine(directory, "client-trusted") },
                TrustedIssuerCertificates = new CertificateTrustList
                { StoreType = "Directory", StorePath = Path.Combine(directory, "client-issuers") },
                RejectedCertificateStore = new CertificateStoreIdentifier
                { StoreType = "Directory", StorePath = Path.Combine(directory, "client-rejected") },
                AutoAcceptUntrustedCertificates = true
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 5000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
        };
        await configuration.ValidateAsync(ApplicationType.Client, ct);
        var description = await CoreClientUtils.SelectEndpointAsync(configuration, Endpoint, false, Telemetry, ct);
        Assert.NotNull(description);
        Assert.Equal(MessageSecurityMode.None, description.SecurityMode);
        return await new DefaultSessionFactory(Telemetry).CreateAsync(configuration,
            new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(configuration)),
            false, false, configuration.ApplicationName, 60000, new UserIdentity(), default, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync(CancellationToken.None);
        host.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
