using Opc.Ua;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

public static class OpcUaEndpointSelector
{
    public static async Task<EndpointDescription> SelectAsync(ApplicationConfiguration configuration,
        OpcUaOptions options, ITelemetryContext telemetry, CancellationToken cancellationToken)
    {
        using var discovery = await DiscoveryClient.CreateAsync(new Uri(options.Endpoint), EndpointConfiguration.Create(configuration), telemetry,
            ct: cancellationToken);
        var endpoints = await discovery.GetEndpointsAsync(default, cancellationToken);
        var mode = Enum.Parse<MessageSecurityMode>(options.Security.Mode);
        var policy = mode == MessageSecurityMode.None ? SecurityPolicies.None : SecurityPolicies.Basic256Sha256;
        var selected = (endpoints.ToArray() ?? []).FirstOrDefault(endpoint => endpoint.SecurityMode == mode
            && endpoint.SecurityPolicyUri == policy
            && Uri.TryCreate(endpoint.EndpointUrl, UriKind.Absolute, out var address)
            && address.Scheme == "opc.tcp");
        if (selected is null)
            throw new ServiceResultException(StatusCodes.BadSecurityPolicyRejected,
                $"No matching endpoint for OPC UA mode {mode} and policy {policy}; refusing security fallback.");

        // Discovery advertises the server's own address, which may be behind Docker NAT.
        // Keep the configured address; session creation still validates the server's certificate.
        selected.EndpointUrl = options.Endpoint;
        return selected;
    }
}
