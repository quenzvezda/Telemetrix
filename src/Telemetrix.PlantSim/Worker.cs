using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Configuration;
using Telemetrix.PlantSim.Server;
using Telemetrix.PlantSim.Model;
using System.Threading.Channels;

namespace Telemetrix.PlantSim;

public sealed class Worker(IOptions<PlantSimOptions> options, ITelemetryContext telemetry,
    TimeProvider timeProvider, PumpingLine line, ILogger<Worker> logger) : BackgroundService
{
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<uint> restarts = Channel.CreateBounded<uint>(1);
    private int restartPending;

    public bool TryRestart(uint delaySeconds)
    {
        if (delaySeconds > 5 || Interlocked.CompareExchange(ref restartPending, 1, 0) != 0) return false;
        return restarts.Writer.TryWrite(delaySeconds);
    }

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
            while (!stoppingToken.IsCancellationRequested)
            {
                using var server = new PlantServer(telemetry, timeProvider, line, options.Value.Seed, TryRestart);
                uint delaySeconds;
                try
                {
                    await application.StartAsync(server, stoppingToken);
                    Interlocked.Exchange(ref restartPending, 0);
                    logger.LogInformation("PlantSim OPC UA server started at {Endpoint}", options.Value.Endpoint);
                    Ready.TrySetResult();
                    using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
                    var requested = restarts.Reader.ReadAsync(cycle.Token).AsTask();
                    try
                    {
                        while (true)
                        {
                            var tick = timer.WaitForNextTickAsync(cycle.Token).AsTask();
                            if (await Task.WhenAny(tick, requested) == requested)
                            {
                                delaySeconds = await requested;
                                // Let the Call response leave the server before dropping its sessions.
                                await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, stoppingToken);
                                break;
                            }
                            if (!await tick) return;
                            server.PlantNodes!.Update();
                        }
                    }
                    finally { await cycle.CancelAsync(); }
                }
                finally { await application.StopAsync(CancellationToken.None); }
                logger.LogInformation("PlantSim restarting after {DelaySeconds} seconds", delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), timeProvider, stoppingToken);
            }
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
                DiagnosticsEnabled = false,
                MaxRegistrationInterval = 0
            }
        };
}
