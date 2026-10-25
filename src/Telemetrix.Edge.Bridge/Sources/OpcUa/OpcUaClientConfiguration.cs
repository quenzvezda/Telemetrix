using Opc.Ua;
using Opc.Ua.Configuration;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

public static class OpcUaClientConfiguration
{
    public static async Task<ApplicationConfiguration> CreateAsync(OpcUaOptions settings,
        ITelemetryContext telemetry, CancellationToken cancellationToken)
    {
        var pkiPath = Path.GetFullPath(settings.Security.PkiRoot);
        var configuration = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = "Telemetrix.Edge.Bridge",
            ApplicationUri = $"urn:{Environment.MachineName}:Telemetrix.Edge.Bridge",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificates =
                [
                    new CertificateIdentifier
                    {
                        StoreType = "Directory", StorePath = Path.Combine(pkiPath, "own"),
                        SubjectName = "CN=Telemetrix.Edge.Bridge"
                    }
                ],
                TrustedPeerCertificates = new CertificateTrustList
                { StoreType = "Directory", StorePath = Path.Combine(pkiPath, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList
                { StoreType = "Directory", StorePath = Path.Combine(pkiPath, "issuers") },
                RejectedCertificateStore = new CertificateStoreIdentifier
                { StoreType = "Directory", StorePath = Path.Combine(pkiPath, "rejected") },
                AutoAcceptUntrustedCertificates = settings.Security.AutoAcceptUntrusted,
                AddAppCertToTrustedStore = false
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 5000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
        };
        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken);
        var application = new ApplicationInstance(configuration, telemetry);
        if (!await application.CheckApplicationInstanceCertificatesAsync(false, null, cancellationToken))
            throw new InvalidOperationException("Bridge could not create its application certificate.");
        return configuration;
    }
}
