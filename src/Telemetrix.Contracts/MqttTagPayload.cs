using System.Text.Json;

namespace Telemetrix.Contracts;

public sealed record MqttTagPayload(JsonElement Value, DateTimeOffset SourceTimestamp, string Quality);

public static class MqttQuality
{
    public const string Good = "good";
    public const string Uncertain = "uncertain";
    public const string Bad = "bad";
}
