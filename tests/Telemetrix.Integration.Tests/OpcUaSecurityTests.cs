using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using System.Security.Cryptography.X509Certificates;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Integration.Tests;

public class OpcUaSecurityTests
{
    [Fact]
    public async Task CertificatesPersistAndSecureTrustCanBeGrantedWithoutRestart()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(1));
        var ct = deadline.Token;
        await using var server = new PlantSimFixture(secure: true, listenOnAllInterfaces: true);
        await server.StartAsync(ct);
        var clientRoot = Path.Combine(server.PkiPath, "client", "pki");
        var options = new OpcUaOptions { Endpoint = server.Endpoint };
        options.Security.PkiRoot = clientRoot;
        var log = new CertificateLog();
        var configuration = await OpcUaClientConfiguration.CreateAsync(options, server.Telemetry, ct, log);
        var clientCertificate = Assert.Single(Directory.GetFiles(Path.Combine(clientRoot, "own", "certs")));
        var serverCertificate = Assert.Single(Directory.GetFiles(Path.Combine(server.PkiPath, "own", "certs")));
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(serverCertificate);
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames().ToArray();
        Assert.Contains("localhost", names);
        Assert.Contains("plantsim", names);
        var ownBytes = await File.ReadAllBytesAsync(clientCertificate, ct);
        await OpcUaClientConfiguration.CreateAsync(options, server.Telemetry, ct);
        Assert.Equal(ownBytes, await File.ReadAllBytesAsync(clientCertificate, ct));
        var description = await OpcUaEndpointSelector.SelectAsync(configuration, options, server.Telemetry, ct);
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, description.SecurityMode);
        Assert.Equal(SecurityPolicies.Basic256Sha256, description.SecurityPolicyUri);

        Task<ISession> Connect() => new DefaultSessionFactory(server.Telemetry).CreateAsync(configuration,
            new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(configuration)),
            false, true, "Telemetrix.Security.Test", 60000, new UserIdentity(), default, ct);

        Trust(clientCertificate, server.PkiPath);
        await Assert.ThrowsAsync<ServiceResultException>(Connect);
        Assert.Contains(log.Entries, entry => entry.Contains("CN=Telemetrix.PlantSim", StringComparison.Ordinal)
            && entry.Contains(certificate.Thumbprint, StringComparison.Ordinal));
        // Reverse the trust direction to prove the server rejects this client too.
        File.Delete(Path.Combine(server.PkiPath, "trusted", "certs", Path.GetFileName(clientCertificate)));
        Trust(serverCertificate, clientRoot);
        await Assert.ThrowsAsync<ServiceResultException>(Connect);
        while (!Directory.Exists(Path.Combine(server.PkiPath, "rejected", "certs")) ||
               Directory.GetFiles(Path.Combine(server.PkiPath, "rejected", "certs")).Length == 0)
            await Task.Delay(50, ct);
        Trust(clientCertificate, server.PkiPath);
        using var session = await Connect();
        var value = await session.ReadValueAsync(new NodeId("Line01.Pump01.Running", 2), ct);
        Assert.True(StatusCode.IsGood(value.StatusCode));
    }

    private static void Trust(string certificate, string root)
    {
        var store = Directory.CreateDirectory(Path.Combine(root, "trusted", "certs"));
        File.Copy(certificate, Path.Combine(store.FullName, Path.GetFileName(certificate)), true);
    }

    private sealed class CertificateLog : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue(formatter(state, exception));
    }

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
