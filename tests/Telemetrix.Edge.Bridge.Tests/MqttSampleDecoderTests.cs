using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Telemetrix.Edge.Bridge.Sources.Mqtt;

namespace Telemetrix.Edge.Bridge.Tests;

public sealed class MqttSampleDecoderTests
{
    [Fact]
    public void MalformedFloodIsCountedRateLimitedAndFollowedByValidSample()
    {
        var clock = new FakeTimeProvider();
        var logger = new Logs();
        using var decoder = new MqttSampleDecoder(clock, logger);
        long measured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "bridge.mqtt.rejected") observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => measured += value);
        listener.Start();
        for (var i = 0; i < 100; i++) Assert.Null(decoder.Decode("plant/a/b/c/d", "{"u8.ToArray()));
        Assert.Equal(100, decoder.RejectedCount);
        Assert.Equal(100, measured);
        Assert.Equal(1, logger.Warnings);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(decoder.Decode("bad-topic", "{}"u8.ToArray()));
        Assert.Equal(2, logger.Warnings);
        var sample = decoder.Decode("plant/a/b/c/d", Encoding.UTF8.GetBytes(
            """{"value":true,"sourceTimestamp":"2026-10-25T00:00:00Z","quality":"good"}"""));
        Assert.NotNull(sample);
        Assert.Equal(1d, sample.Value);
        Assert.Equal(101, decoder.RejectedCount);
    }

    private sealed class Logs : ILogger
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { if (logLevel == LogLevel.Warning) Warnings++; }
    }
}
