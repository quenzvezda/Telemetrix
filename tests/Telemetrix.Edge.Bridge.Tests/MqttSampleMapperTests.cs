using System.Text;
using System.Text.Json;
using Telemetrix.Edge.Bridge.Sources.Mqtt;

namespace Telemetrix.Edge.Bridge.Tests;

public sealed class MqttSampleMapperTests
{
    [Theory]
    [InlineData("52.8", "good", 52.8, 0u, false)]
    [InlineData("true", "uncertain", 1d, 0x40000000u, false)]
    [InlineData("false", "bad", 0d, 0x80000000u, false)]
    [InlineData("10", "other", 10d, 0x80000000u, true)]
    public void MapsIdentityValueQualityAndBothTimes(string value, string quality, double expected,
        uint status, bool unknown)
    {
        var now = DateTimeOffset.UtcNow;
        var sample = MqttSampleMapper.Map("plant/Plant01/Line01/Pump01/Running",
            Encoding.UTF8.GetBytes($$"""{"value":{{value}},"quality":"{{quality}}","sourceTimestamp":"2026-10-25T12:00:00+07:00"}"""), now, out var warning);
        Assert.Equal("Plant01", sample.Plant);
        Assert.Equal("Line01", sample.Line);
        Assert.Equal("Pump01", sample.Asset);
        Assert.Equal("Running", sample.Tag);
        Assert.Equal(expected, sample.Value);
        Assert.Equal(status, sample.StatusCode);
        Assert.Equal(unknown, warning);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.Zero), sample.SourceTimestamp);
        Assert.Equal(TimeSpan.Zero, sample.SourceTimestamp.Offset);
        Assert.Equal(now, sample.ServerTimestamp);
    }

    [Theory]
    [InlineData("plant/a/b/c")]
    [InlineData("plant/a/b/c/d/e")]
    [InlineData("plant/a//c/d")]
    [InlineData("other/a/b/c/d")]
    [InlineData("plant/a/+/c/d")]
    public void RejectsMalformedTopics(string topic) => Assert.Throws<JsonException>(() =>
        MqttSampleMapper.Map(topic, ValidPayload(), DateTimeOffset.UtcNow, out _));

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{bad")]
    [InlineData("{\"value\":\"text\",\"quality\":\"good\",\"sourceTimestamp\":\"2026-10-25T00:00:00Z\"}")]
    [InlineData("{\"value\":1e999,\"quality\":\"good\",\"sourceTimestamp\":\"2026-10-25T00:00:00Z\"}")]
    public void RejectsMalformedPayloads(string payload) => Assert.Throws<JsonException>(() =>
        MqttSampleMapper.Map("plant/a/b/c/d", Encoding.UTF8.GetBytes(payload), DateTimeOffset.UtcNow, out _));

    private static byte[] ValidPayload() => Encoding.UTF8.GetBytes(
        """{"value":1,"quality":"good","sourceTimestamp":"2026-10-25T00:00:00Z"}""");
}
