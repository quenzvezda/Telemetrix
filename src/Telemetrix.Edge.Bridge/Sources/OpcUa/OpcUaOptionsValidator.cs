using Microsoft.Extensions.Options;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

internal sealed class OpcUaOptionsValidator(IHostEnvironment environment, ILogger<OpcUaOptionsValidator> logger)
    : IValidateOptions<OpcUaOptions>
{
    public ValidateOptionsResult Validate(string? name, OpcUaOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Plant))
        {
            failures.Add("OpcUa:Plant is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Line))
        {
            failures.Add("OpcUa:Line is required.");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            failures.Add("OpcUa:Endpoint must be an absolute opc.tcp:// URL with a host.");
        }

        if (options.Security.Mode is not ("None" or "Sign" or "SignAndEncrypt"))
        {
            failures.Add("OpcUa:Security:Mode must be None, Sign or SignAndEncrypt.");
        }
        if (options.Security.Policy != "Basic256Sha256")
            failures.Add("OpcUa:Security:Policy must be Basic256Sha256 (None mode selects the None policy).");
        if (string.IsNullOrWhiteSpace(options.Security.PkiRoot))
            failures.Add("OpcUa:Security:PkiRoot is required.");
        if (!environment.IsDevelopment() && options.Security.Mode == "None")
            failures.Add("OpcUa:Security:Mode=None is allowed only in Development.");
        if (!environment.IsDevelopment() && options.Security.AutoAcceptUntrusted)
            failures.Add("OpcUa:Security:AutoAcceptUntrusted is allowed only in Development.");
        if (environment.IsDevelopment() && (options.Security.Mode == "None" || options.Security.AutoAcceptUntrusted))
            logger.LogWarning("OPC UA connection is unsecured or accepts untrusted certificates; Development only");

        if (options.SamplingIntervalMs <= 0)
        {
            failures.Add("OpcUa:SamplingIntervalMs must be greater than zero.");
        }

        if (options.PublishingIntervalMs <= 0)
        {
            failures.Add("OpcUa:PublishingIntervalMs must be greater than zero.");
        }

        if (options.QueueSize == 0)
        {
            failures.Add("OpcUa:QueueSize must be greater than zero.");
        }

        if (options.Tags.Count == 0 && string.IsNullOrWhiteSpace(options.BrowseStartNode))
        {
            failures.Add("OpcUa:Tags must contain at least one tag, or OpcUa:BrowseStartNode must be set.");
        }

        for (var i = 0; i < options.Tags.Count; i++)
        {
            var tag = options.Tags[i];
            if (string.IsNullOrWhiteSpace(tag.Asset))
            {
                failures.Add($"OpcUa:Tags:{i}:Asset is required.");
            }

            if (string.IsNullOrWhiteSpace(tag.Tag))
            {
                failures.Add($"OpcUa:Tags:{i}:Tag is required.");
            }

            if (string.IsNullOrWhiteSpace(tag.NodeId))
            {
                failures.Add($"OpcUa:Tags:{i}:NodeId is required.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
