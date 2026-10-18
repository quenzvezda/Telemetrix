using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Tests;

public class SampleTests
{
    [Fact]
    public void TimestampsAreUtcAndPreserveTheSuppliedInstants()
    {
        var source = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(7));
        var server = source.AddMilliseconds(4).ToOffset(TimeSpan.FromHours(-3));

        var sample = new Sample("Plant01", "Line01", "Pump01", "FlowRate", 52.8,
            source, server, 0);

        Assert.Equal(TimeSpan.Zero, sample.SourceTimestamp.Offset);
        Assert.Equal(TimeSpan.Zero, sample.ServerTimestamp.Offset);
        Assert.Equal(source, sample.SourceTimestamp);
        Assert.Equal(server, sample.ServerTimestamp);
    }

    [Fact]
    public void MissingValuePreservesTheRawStatusCode()
    {
        var timestamp = new DateTimeOffset(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);

        var sample = new Sample("Plant01", "Line01", "Tank01", "Temperature", null,
            timestamp, timestamp, 0x80000000);

        Assert.Null(sample.Value);
        Assert.Equal(0x80000000u, sample.StatusCode);
    }
}
