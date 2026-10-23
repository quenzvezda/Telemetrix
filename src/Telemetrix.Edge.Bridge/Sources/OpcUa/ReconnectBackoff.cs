namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

internal static class ReconnectBackoff
{
    public static TimeSpan Delay(int attempt, double random)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        if (random is < 0 or > 1 || double.IsNaN(random))
            throw new ArgumentOutOfRangeException(nameof(random));
        var seconds = Math.Min(15, Math.Pow(2, Math.Min(attempt, 4)));
        return TimeSpan.FromSeconds(Math.Min(15, seconds * (0.8 + random * 0.4)));
    }
}
