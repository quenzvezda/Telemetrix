using System.Text.Json;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Tests;

public class PlantEventJsonTests
{
    [Theory]
    [InlineData(PlantEventType.ConnectionLost)]
    [InlineData(PlantEventType.ConnectionRestored)]
    [InlineData(PlantEventType.Gap)]
    public void EventsRoundTripWithVersionedStringTypesAndUtcGapBounds(PlantEventType type)
    {
        var now = new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.FromHours(7));
        var expected = new PlantEvent(1, Guid.NewGuid(), type, "Plant01", "Line01", "Pump01",
            "opc.tcp://localhost:50000", now, now.AddSeconds(-30), now, "buffer-capacity");
        var bytes = TelemetryJson.Serialize(expected);
        Assert.Equal(expected, TelemetryJson.DeserializePlantEvent(bytes));
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(type.ToString(), document.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var name in new[] { "occurredAt", "gapStart", "gapEnd" })
            Assert.EndsWith("Z", document.RootElement.GetProperty(name).GetString());
    }
}
