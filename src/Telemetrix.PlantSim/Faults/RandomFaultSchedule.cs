using Telemetrix.PlantSim.Model;

namespace Telemetrix.PlantSim.Faults;

/// <summary>Seeded exponential inter-arrival times, advanced by the injected clock.</summary>
public sealed class RandomFaultSchedule
{
    private readonly RandomFaultOptions options;
    private readonly PumpingLine line;
    private readonly TimeProvider clock;
    private readonly Random rng;
    private DateTimeOffset tripAt;
    private DateTimeOffset spikeAt;
    private DateTimeOffset badAt;
    private DateTimeOffset? restartAt;

    public RandomFaultSchedule(RandomFaultOptions options, PumpingLine line, TimeProvider clock)
    {
        this.options = options;
        this.line = line;
        this.clock = clock;
        rng = new Random(options.Seed);
        var now = clock.GetUtcNow();
        tripAt = Next(now, options.PumpTripsPerHour);
        spikeAt = Next(now, options.VibrationSpikesPerHour);
        badAt = Next(now, options.BadQualityPerHour);
    }

    public IReadOnlyList<string> Tick()
    {
        if (!options.Enabled) return [];
        List<string> faults = [];
        var now = clock.GetUtcNow();
        if (restartAt is { } restart && now >= restart)
        {
            line.StartPump();
            restartAt = null;
            faults.Add("StartPump");
        }
        if (now >= tripAt)
        {
            line.TripPump();
            restartAt = now + options.PumpRestartDelay;
            tripAt = Next(now, options.PumpTripsPerHour);
            faults.Add("TripPump");
        }
        if (now >= spikeAt)
        {
            line.Spike("Pump01.Vibration");
            spikeAt = Next(now, options.VibrationSpikesPerHour);
            faults.Add("Spike");
        }
        if (now >= badAt)
        {
            line.SetBadQuality("Tank01.Temperature", options.BadQualityDuration);
            badAt = Next(now, options.BadQualityPerHour);
            faults.Add("SetBadQuality");
        }
        return faults;
    }

    private DateTimeOffset Next(DateTimeOffset after, double perHour) => perHour == 0
        ? DateTimeOffset.MaxValue
        : after + TimeSpan.FromHours(-Math.Log(1 - rng.NextDouble()) / perHour);
}
