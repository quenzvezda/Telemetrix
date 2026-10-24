using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Configuration;
using Telemetrix.PlantSim.Server;
using Telemetrix.PlantSim.Model;

namespace Telemetrix.PlantSim;

public sealed class Worker(IOptions<PlantSimOptions> options, ITelemetryContext telemetry,
    TimeProvider timeProvider, PumpingLine line, ILogger<Worker> logger) : BackgroundService
{
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ApplicationInstance? application = null;
        try
        {
            var configuration = CreateConfiguration(options.Value, telemetry);
            await configuration.ValidateAsync(ApplicationType.Server, stoppingToken);
            application = new ApplicationInstance(configuration, telemetry);
            if (!await application.CheckApplicationInstanceCertificatesAsync(false, null, stoppingToken))
                throw new InvalidOperationException("PlantSim could not create its application certificate.");
            using var server = new PlantServer(telemetry, timeProvider, line, options.Value.Seed);
            await application.StartAsync(server, stoppingToken);
            logger.LogInformation("PlantSim OPC UA server started at {Endpoint}", options.Value.Endpoint);
            Ready.TrySetResult();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                server.PlantNodes!.Update();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Ready.TrySetException(exception);
            throw;
        }
        finally
        {
            if (application is not null) await application.StopAsync(CancellationToken.None);
        }
    }

    private static ApplicationConfiguration CreateConfiguration(PlantSimOptions settings, ITelemetryContext telemetry) =>
        new(telemetry)
        {
            ApplicationName = "Telemetrix.PlantSim",
            ApplicationUri = $"urn:{Environment.MachineName}:Telemetrix.PlantSim",
            ProductUri = "urn:telemetrix:plantsim",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificates =
                [
                    new CertificateIdentifier
                    {
                        StoreType = "Directory", StorePath = Path.Combine(settings.PkiPath, "own"),
                        SubjectName = "CN=Telemetrix.PlantSim"
                    }
                ],
                TrustedPeerCertificates = new CertificateTrustList
                { StoreType = "Directory", StorePath = Path.Combine(settings.PkiPath, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList
                { StoreType = "Directory", StorePath = Path.Combine(settings.PkiPath, "issuers") },
                RejectedCertificateStore = new CertificateStoreIdentifier
                { StoreType = "Directory", StorePath = Path.Combine(settings.PkiPath, "rejected") },
                AutoAcceptUntrustedCertificates = true
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 5000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = [settings.Endpoint],
                SecurityPolicies =
                [
                    new ServerSecurityPolicy
                    { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
                ],
                UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
                DiagnosticsEnabled = false
            }
        };
}
