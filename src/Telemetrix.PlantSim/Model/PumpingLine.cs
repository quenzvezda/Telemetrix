namespace Telemetrix.PlantSim.Model;

/// <summary>A single pumping line, independent of OPC UA and safe for concurrent fault requests.</summary>
public sealed class PumpingLine(TimeProvider timeProvider)
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> badUntil = [];
    private readonly HashSet<string> spikes = [];
    private bool running = true;
    private double flow = 50;
    private double motorTemperature = 60;
    private double level = 50;
    private double tankTemperature = 22;

    public void TripPump() { lock (gate) running = false; }
    public void StartPump() { lock (gate) running = true; }

    public void SetBadQuality(string tag, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        lock (gate) badUntil[FindTag(tag).Key] = timeProvider.GetUtcNow() + duration;
    }

    public void Spike(string tag)
    {
        var definition = FindTag(tag);
        if (definition.IsBoolean) throw new ArgumentException("A Boolean tag cannot spike.", nameof(tag));
        lock (gate) spikes.Add(definition.Key);
    }

    public IReadOnlyList<TagValue> Step(TimeSpan dt, Random rng)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dt, TimeSpan.Zero);
        lock (gate)
        {
            var seconds = dt.TotalSeconds;
            flow = running
                ? Math.Clamp(flow + (50 - flow) * (1 - Math.Exp(-seconds / 2)) + Noise(rng) * 0.2, 40, 60)
                : 0;
            var pressure = running ? Math.Clamp(1 + 0.075 * flow + Noise(rng) * 0.03, 3.5, 5) : 1;
            var targetTemperature = running ? 45 + 0.6 * flow : 45;
            motorTemperature = Math.Clamp(motorTemperature +
                (targetTemperature - motorTemperature) * (1 - Math.Exp(-seconds / 600)), 45, 80);
            level = Math.Clamp(level + (running ? 0.015 : -0.025) * seconds, 20, 90);
            tankTemperature = Math.Clamp(tankTemperature + Noise(rng) * 0.01 * Math.Sqrt(seconds), 15, 30);
            object[] values = [running, flow, pressure, motorTemperature,
                running ? Math.Clamp(1.5 + flow * 0.015 + Noise(rng) * 0.15, 0.5, 4.5) : 0.5,
                level, tankTemperature];
            var now = timeProvider.GetUtcNow();
            return PlantTags.All.Select((tag, i) => new TagValue(tag,
                spikes.Remove(tag.Key) ? tag.Maximum + Math.Max(1, tag.Maximum * 0.5) : values[i],
                badUntil.TryGetValue(tag.Key, out var until) && now < until)).ToArray();
        }
    }

    private static PlantTag FindTag(string key) =>
        PlantTags.All.FirstOrDefault(tag => tag.Key == key)
        ?? throw new ArgumentException($"Unknown tag '{key}'.", nameof(key));

    private static double Noise(Random rng) =>
        Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
}
