namespace Telemetrix.Edge.Bridge;

public sealed class IngestionOptions
{
    public int BatchQueueCapacity { get; set; } = 64;
}
