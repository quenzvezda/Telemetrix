namespace Telemetrix.Edge.Bridge.Batching;

public sealed class BatchingOptions
{
    public const string SectionName = "Batching";

    public int MaxSamples { get; set; } = 500;
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(5);
}
