using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Batching;

// Assigns one transformation label per asset; batches of other assets stay unlabeled (ADR-0017).
public sealed class TransformationAssignmentOptions
{
    public const string SectionName = "Transformations";

    public Dictionary<string, string?> Assets { get; set; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Labels() => Assets
        .Where(asset => !string.IsNullOrWhiteSpace(asset.Value))
        .ToDictionary(asset => asset.Key, asset => asset.Value!, StringComparer.Ordinal);

    public bool IsValid() => Labels().Values.All(TransformationLabel.IsValid);
}
