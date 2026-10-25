using System.Diagnostics.Metrics;
using System.Text.Json;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Sources.Mqtt;

internal sealed class MqttSampleDecoder(TimeProvider timeProvider, ILogger logger) : IDisposable
{
    private readonly Meter meter = new("Telemetrix.Edge.Bridge.Mqtt");
    private Counter<long>? rejected;
    private long rejectedCount;
    private long? lastWarning;
    public long RejectedCount => Interlocked.Read(ref rejectedCount);

    public Sample? Decode(string topic, byte[] payload)
    {
        try
        {
            var sample = MqttSampleMapper.Map(topic, payload, timeProvider.GetUtcNow(), out var unknown);
            if (unknown) Warn("Unknown MQTT quality mapped to Bad at {Topic}", topic);
            return sample;
        }
        catch (JsonException)
        {
            Interlocked.Increment(ref rejectedCount);
            (rejected ??= meter.CreateCounter<long>("bridge.mqtt.rejected")).Add(1);
            Warn("Rejected malformed MQTT sample at {Topic}", topic);
            return null;
        }
    }

    private void Warn(string message, string topic)
    {
        var now = timeProvider.GetTimestamp();
        if (lastWarning.HasValue && timeProvider.GetElapsedTime(lastWarning.Value, now) < TimeSpan.FromSeconds(10)) return;
        lastWarning = now;
        logger.LogWarning(message, topic);
    }

    public void Dispose() => meter.Dispose();
}
