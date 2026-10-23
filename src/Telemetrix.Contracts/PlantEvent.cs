using System.Text.Json.Serialization;

namespace Telemetrix.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PlantEventType>))]
public enum PlantEventType
{
    ConnectionLost,
    ConnectionRestored,
    Gap
}

public sealed record PlantEvent(int SchemaVersion, Guid EventId, PlantEventType Type,
    string PlantId, string LineId, string AssetId, string Endpoint, DateTimeOffset OccurredAt,
    DateTimeOffset? GapStart = null, DateTimeOffset? GapEnd = null, string? Reason = null)
{
    public const int CurrentSchemaVersion = 1;
}
