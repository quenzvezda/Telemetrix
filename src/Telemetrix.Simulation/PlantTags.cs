namespace Telemetrix.Simulation;

public sealed record PlantTag(string Asset, string Name, string Unit, double Minimum, double Maximum,
    bool IsBoolean = false)
{
    public string Key => $"{Asset}.{Name}";
    public string Identifier => $"Line01.{Key}";
}

public static class PlantTags
{
    public static IReadOnlyList<PlantTag> All { get; } = Array.AsReadOnly<PlantTag>(
    [
        new("Pump01", "Running", "", 0, 1, true),
        new("Pump01", "FlowRate", "m³/h", 40, 60),
        new("Pump01", "DischargePressure", "bar", 3.5, 5),
        new("Pump01", "MotorTemperature", "°C", 45, 80),
        new("Pump01", "Vibration", "mm/s", 0.5, 4.5),
        new("Tank01", "Level", "%", 20, 90),
        new("Tank01", "Temperature", "°C", 15, 30)
    ]);
}

public sealed record TagValue(PlantTag Tag, object Value, bool IsBad = false);
