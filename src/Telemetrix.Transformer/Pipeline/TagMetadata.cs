namespace Telemetrix.Transformer.Pipeline;

public sealed record TagMetadata(int TagId, string AssetId, string Name,
    string? Unit, double? MinValid, double? MaxValid);
