using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;
using Telemetrix.PlantSim.Server;
using Telemetrix.Simulation;
using System.Threading.Channels;

namespace Telemetrix.PlantSim;

public sealed class Worker(IOptions<PlantSimOptions> options, ITelemetryContext telemetry,
    TimeProvider timeProvider, PumpingLine line, ILogger<Worker> logger) : BackgroundService
{
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<uint> restarts = Channel.CreateBounded<uint>(1);
    private int restartPending;
    private int running;
    public bool IsReady => Volatile.Read(ref running) != 0;

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
            if (options.Value.SecurityMode == "None")
                logger.LogWarning("PlantSim OPC UA connection is unsecured; Development only");
            await configuration.ValidateAsync(ApplicationType.Server, stoppingToken);
            await EnsureCertificateAsync(configuration, stoppingToken);
            application = new ApplicationInstance(configuration, telemetry);
            if (!await application.CheckApplicationInstanceCertificatesAsync(false, null, stoppingToken))
                throw new InvalidOperationException("PlantSim could not create its application certificate.");
            configuration.CertificateManager!.AcceptError = (certificate, error) =>
            {
                logger.LogWarning("OPC UA rejected peer certificate: subject {Subject}, thumbprint {Thumbprint}, error {StatusCode}",
                    certificate.Subject, certificate.Thumbprint, error.StatusCode);
                return false;
            };
            while (!stoppingToken.IsCancellationRequested)
            {
                using var server = new PlantServer(telemetry, timeProvider, line, options.Value.Seed, TryRestart);
                uint delaySeconds;
                try
                {
                    await application.StartAsync(server, stoppingToken);
                    Volatile.Write(ref running, 1);
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
                finally
                {
                    Volatile.Write(ref running, 0);
                    await application.StopAsync(CancellationToken.None);
                }
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
            Volatile.Write(ref running, 0);
            if (application is not null) await application.StopAsync(CancellationToken.None);
        }
    }

    private async Task EnsureCertificateAsync(ApplicationConfiguration configuration, CancellationToken ct)
    {
        var identifier = configuration.SecurityConfiguration.ApplicationCertificates[0];
        using var existing = await CertificateIdentifierResolver.LoadPrivateKeyAsync(identifier, null,
            configuration.ApplicationUri, telemetry, ct);
        if (existing is not null) return;
        // The SDK normalizes localhost to the machine name for opc.tcp. Preserve
        // both public connection names explicitly in the certificate SAN. Put the
        // machine name first: the factory normalizes the first domain entry.
        using var certificate = DefaultCertificateFactory.Instance.CreateApplicationCertificate(configuration.ApplicationUri!,
            "Telemetrix.PlantSim", "CN=Telemetrix.PlantSim",
            [Environment.MachineName, "localhost", "plantsim", new Uri(options.Value.Endpoint).Host]).CreateForRSA();
        using var store = CertificateIdentifierResolver.OpenStore(identifier, telemetry)
            ?? throw new InvalidOperationException("PlantSim could not open its application certificate store.");
        await store.AddAsync(certificate, null, ct);
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
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = false
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 5000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = [settings.Endpoint],
                AlternateBaseAddresses =
                [
                    new UriBuilder(settings.Endpoint) { Host = "localhost" }.Uri.ToString(),
                    new UriBuilder(settings.Endpoint) { Host = "plantsim" }.Uri.ToString()
                ],
                SecurityPolicies =
                [
                    new ServerSecurityPolicy
                    {
                        SecurityMode = settings.SecurityMode == "None" ? MessageSecurityMode.None : MessageSecurityMode.SignAndEncrypt,
                        SecurityPolicyUri = settings.SecurityMode == "None" ? SecurityPolicies.None : SecurityPolicies.Basic256Sha256
                    }
                ],
                UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
                DiagnosticsEnabled = false,
                ShutdownDelay = 0,
                MaxRegistrationInterval = 0
            }
        };
}
