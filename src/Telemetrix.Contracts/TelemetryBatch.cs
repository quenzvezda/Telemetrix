namespace Telemetrix.Contracts;

public sealed record TelemetryBatch
{
    public const int CurrentSchemaVersion = 1;

    public TelemetryBatch(
        int schemaVersion,
        Guid batchId,
        string source,
        string endpoint,
        string plantId,
        string lineId,
        string assetId,
        DateTimeOffset createdAt,
        IReadOnlyList<BatchSample> samples)
    {
        SchemaVersion = schemaVersion;
        BatchId = batchId;
        Source = source;
        Endpoint = endpoint;
        PlantId = plantId;
        LineId = lineId;
        AssetId = assetId;
        CreatedAt = createdAt.ToUniversalTime();
        Samples = Array.AsReadOnly(samples.ToArray());
    }

    public int SchemaVersion { get; }
    public Guid BatchId { get; }
    public string Source { get; }
    public string Endpoint { get; }
    public string PlantId { get; }
    public string LineId { get; }
    public string AssetId { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<BatchSample> Samples { get; }
}
