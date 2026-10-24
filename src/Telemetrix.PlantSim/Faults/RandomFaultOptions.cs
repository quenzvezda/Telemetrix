namespace Telemetrix.PlantSim.Faults;

public sealed class RandomFaultOptions
{
    public bool Enabled { get; set; } = true;
    public double PumpTripsPerHour { get; set; } = 1;
    public TimeSpan PumpRestartDelay { get; set; } = TimeSpan.FromMinutes(2);
    public double VibrationSpikesPerHour { get; set; } = 6;
    public double BadQualityPerHour { get; set; } = 2;
    public TimeSpan BadQualityDuration { get; set; } = TimeSpan.FromSeconds(30);
    public int Seed { get; set; } = 123;
}
