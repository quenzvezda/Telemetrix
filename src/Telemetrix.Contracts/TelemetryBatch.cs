using System.Text.Json.Serialization;

namespace Telemetrix.Contracts;

public sealed record TelemetryBatch
{
    // Version 2 adds the optional transformation label; unlabeled batches stay version 1 (ADR-0017).
    public const int CurrentSchemaVersion = 2;
    public const int UnlabeledSchemaVersion = 1;

    public TelemetryBatch(
        int schemaVersion,
        Guid batchId,
        string source,
        string endpoint,
        string plantId,
        string lineId,
        string assetId,
        DateTimeOffset createdAt,
        IReadOnlyList<BatchSample> samples,
        string? transformation = null)
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
        Transformation = transformation;
    }

    public static int SchemaVersionFor(string? transformation) =>
        transformation is null ? UnlabeledSchemaVersion : CurrentSchemaVersion;

    public int SchemaVersion { get; }
    public Guid BatchId { get; }
    public string Source { get; }
    public string Endpoint { get; }
    public string PlantId { get; }
    public string LineId { get; }
    public string AssetId { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<BatchSample> Samples { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transformation { get; }
}
