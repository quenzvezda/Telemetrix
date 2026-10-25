using System.Text.Json;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Sources.Mqtt;

public static class MqttSampleMapper
{
    public static Sample Map(string topic, ReadOnlySpan<byte> bytes, DateTimeOffset receivedAt,
        out bool unknownQuality)
    {
        var parts = topic.Split('/');
        if (parts.Length != 5 || parts[0] != "plant" || parts.Any(part =>
            string.IsNullOrWhiteSpace(part) || part.IndexOfAny(['+', '#', '\0']) >= 0))
            throw new JsonException("Expected plant/{plant}/{line}/{asset}/{tag}.");
        var payload = TelemetryJson.DeserializeMqtt(bytes);
        if (payload.SourceTimestamp == default || string.IsNullOrWhiteSpace(payload.Quality))
            throw new JsonException("A source timestamp and quality are required.");
        double? value = payload.Value.ValueKind switch
        {
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Null => null,
            JsonValueKind.Number when payload.Value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            _ => throw new JsonException("MQTT values must be finite numbers, booleans or null.")
        };
        unknownQuality = payload.Quality is not (MqttQuality.Good or MqttQuality.Uncertain or MqttQuality.Bad);
        var statusCode = payload.Quality switch
        {
            MqttQuality.Good => 0u,
            MqttQuality.Uncertain => 0x40000000u,
            _ => 0x80000000u
        };
        return new Sample(parts[1], parts[2], parts[3], parts[4], value,
            payload.SourceTimestamp, receivedAt, statusCode);
    }
}
