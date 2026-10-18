using Microsoft.Extensions.Options;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

internal sealed class OpcUaOptionsValidator : IValidateOptions<OpcUaOptions>
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

        if (!string.Equals(options.SecurityMode, "None", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("OpcUa:SecurityMode must be None in M2; secure connections are added in M9.");
        }

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
