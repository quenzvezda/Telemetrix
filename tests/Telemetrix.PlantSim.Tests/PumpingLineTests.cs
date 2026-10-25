using Microsoft.Extensions.Time.Testing;
using Telemetrix.Simulation;

namespace Telemetrix.PlantSim.Tests;

public sealed class PumpingLineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1000)]
    public void TenMinutesStayInNormalRanges(int seed)
    {
        var line = new PumpingLine(TimeProvider.System);
        var rng = new Random(seed);
        for (var tick = 0; tick < 600; tick++)
        foreach (var sample in line.Step(TimeSpan.FromSeconds(1), rng))
        {
            Assert.False(sample.IsBad);
            Assert.InRange(Convert.ToDouble(sample.Value), sample.Tag.Minimum, sample.Tag.Maximum);
        }
    }

    [Fact]
    public void TripAndStartCorrelateFlowTemperatureAndLevel()
    {
        var line = new PumpingLine(TimeProvider.System);
        var rng = new Random(42);
        var before = line.Step(TimeSpan.FromSeconds(1), rng);
        line.TripPump();
        var stopped = line.Step(TimeSpan.FromSeconds(1), rng);
        Assert.False((bool)stopped[0].Value);
        Assert.Equal(0d, stopped[1].Value);
        foreach (var index in new[] { 2, 3, 5 })
            Assert.True((double)stopped[index].Value < (double)before[index].Value);
        for (var i = 0; i < 60; i++) stopped = line.Step(TimeSpan.FromSeconds(1), rng);
        line.StartPump();
        var started = line.Step(TimeSpan.FromMinutes(3), rng);
        Assert.True((bool)started[0].Value);
        Assert.True((double)started[3].Value > (double)stopped[3].Value);
        Assert.True((double)started[5].Value > (double)stopped[5].Value);
    }

    [Fact]
    public void SameSeedProducesSameValues()
    {
        var first = new PumpingLine(TimeProvider.System);
        var second = new PumpingLine(TimeProvider.System);
        var a = new Random(123);
        var b = new Random(123);
        for (var i = 0; i < 600; i++)
            Assert.Equal(first.Step(TimeSpan.FromSeconds(1), a), second.Step(TimeSpan.FromSeconds(1), b));
    }

    [Fact]
    public void BadQualityExpiresAndSpikeReplacesExactlyOneUpdate()
    {
        var clock = new FakeTimeProvider();
        var line = new PumpingLine(clock);
        var rng = new Random(1);
        line.SetBadQuality("Tank01.Temperature", TimeSpan.FromSeconds(30));
        line.Spike("Pump01.Vibration");
        var first = line.Step(TimeSpan.FromSeconds(1), rng);
        Assert.True(first[6].IsBad);
        Assert.True((double)first[4].Value > first[4].Tag.Maximum);
        clock.Advance(TimeSpan.FromSeconds(29));
        var second = line.Step(TimeSpan.FromSeconds(1), rng);
        Assert.True(second[6].IsBad);
        Assert.InRange((double)second[4].Value, 0.5, 4.5);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(line.Step(TimeSpan.FromSeconds(1), rng)[6].IsBad);
    }

    [Fact]
    public void InvalidFaultRequestsDoNotChangeTheLine()
    {
        var line = new PumpingLine(TimeProvider.System);
        Assert.Throws<ArgumentException>(() => line.Spike("missing"));
        Assert.Throws<ArgumentException>(() => line.Spike("Pump01.Running"));
        Assert.Throws<ArgumentOutOfRangeException>(() => line.SetBadQuality("Tank01.Level", TimeSpan.Zero));
    }
}
