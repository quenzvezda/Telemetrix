namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class BufferReplayWorker(StoreAndForwardQueue queue) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => queue.ReplayAsync(stoppingToken);
}
