using Microsoft.Extensions.Time.Testing;
using Telemetrix.PlantSim.Faults;
using Telemetrix.PlantSim.Model;

namespace Telemetrix.PlantSim.Tests;

public sealed class RandomFaultScheduleTests
{
    [Fact]
    public void SeededSixHoursHaveEveryFaultAndAutomaticPumpRestarts()
    {
        var first = RunSixHours(new RandomFaultOptions());
        Assert.Contains("TripPump", first);
        Assert.Contains("StartPump", first);
        Assert.Contains("Spike", first);
        Assert.Contains("SetBadQuality", first);
        Assert.Equal(first, RunSixHours(new RandomFaultOptions()));
    }

    [Fact]
    public void DisabledSchedulerNeverInjectsAFault() =>
        Assert.Empty(RunSixHours(new RandomFaultOptions { Enabled = false }));

    [Fact]
    public void ZeroRatesNeverInjectAFault() =>
        Assert.Empty(RunSixHours(new RandomFaultOptions
        { PumpTripsPerHour = 0, VibrationSpikesPerHour = 0, BadQualityPerHour = 0 }));

    private static List<string> RunSixHours(RandomFaultOptions options)
    {
        var clock = new FakeTimeProvider();
        var line = new PumpingLine(clock);
        var schedule = new RandomFaultSchedule(options, line, clock);
        var rng = new Random(1);
        List<string> faults = [];
        for (var tick = 0; tick < 6 * 60 * 60; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var generated = schedule.Tick();
            faults.AddRange(generated.Select(fault => $"{fault}"));
            var samples = line.Step(TimeSpan.FromSeconds(1), rng);
            if (generated.Contains("TripPump")) Assert.False((bool)samples[0].Value);
            if (generated.Contains("StartPump") && !generated.Contains("TripPump")) Assert.True((bool)samples[0].Value);
            if (generated.Contains("Spike")) Assert.True((double)samples[4].Value > 4.5);
            if (generated.Contains("SetBadQuality")) Assert.True(samples[6].IsBad);
        }
        return faults;
    }
}
