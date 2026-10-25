using System.Text.Json;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Tests;

public sealed class MqttPayloadTests
{
    [Theory]
    [InlineData("52.8", JsonValueKind.Number)]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("false", JsonValueKind.False)]
    public void PayloadRoundTripsWithUtcSourceTime(string value, JsonValueKind kind)
    {
        using var document = JsonDocument.Parse(value);
        var time = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(7));
        var payload = new MqttTagPayload(document.RootElement.Clone(), time, MqttQuality.Good);
        var bytes = TelemetryJson.Serialize(payload);
        var restored = TelemetryJson.DeserializeMqtt(bytes);
        Assert.Equal(kind, restored.Value.ValueKind);
        Assert.Equal(value, restored.Value.GetRawText());
        Assert.Equal(time, restored.SourceTimestamp);
        Assert.Equal(TimeSpan.Zero, restored.SourceTimestamp.Offset);
        Assert.Equal(MqttQuality.Good, restored.Quality);
        using var json = JsonDocument.Parse(bytes);
        Assert.EndsWith("Z", json.RootElement.GetProperty("sourceTimestamp").GetString());
    }
}
