using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Publishing;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

public sealed class PlantEventTracker(IOptions<OpcUaOptions> options, IPlantEventPublisher publisher,
    TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, AssetState> assets = new(StringComparer.Ordinal);
    private bool connected;
    private bool interrupted;
    private string? reason;

    public void TrackAssets(IEnumerable<string> assetIds)
    {
        gate.Wait();
        try
        {
            foreach (var asset in assetIds) assets.TryAdd(asset, new AssetState());
        }
        finally { gate.Release(); }
    }

    public async Task LostAsync(string cause, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!connected || interrupted) return;
            interrupted = true;
            reason = cause;
            foreach (var (asset, state) in assets)
            {
                state.GapStart = state.LastSample;
                state.AwaitingGap = false;
                await PublishAsync(asset, PlantEventType.ConnectionLost, null, null, ct);
            }
        }
        finally { gate.Release(); }
    }

    public async Task RestoredAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            connected = true;
            if (!interrupted) return;
            foreach (var (asset, state) in assets)
            {
                await PublishAsync(asset, PlantEventType.ConnectionRestored, null, null, ct);
                state.AwaitingGap = state.GapStart.HasValue;
            }
            interrupted = false;
        }
        finally { gate.Release(); }
    }

    public async Task SampleAsync(Sample sample, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!assets.TryGetValue(sample.Asset, out var state)) return;
            if (interrupted) return;
            if (state.AwaitingGap && sample.SourceTimestamp > state.GapStart)
            {
                await PublishAsync(sample.Asset, PlantEventType.Gap, state.GapStart, sample.SourceTimestamp, ct);
                state.AwaitingGap = false;
            }
            if (state.LastSample is null || sample.SourceTimestamp > state.LastSample)
                state.LastSample = sample.SourceTimestamp;
        }
        finally { gate.Release(); }
    }

    private Task PublishAsync(string asset, PlantEventType type, DateTimeOffset? start,
        DateTimeOffset? end, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var settings = options.Value;
        return publisher.PublishAsync(new PlantEvent(1, Guid.CreateVersion7(now), type,
            settings.Plant, settings.Line, asset, settings.Endpoint, now, start, end, reason), ct);
    }

    public void Dispose() => gate.Dispose();

    private sealed class AssetState
    {
        public DateTimeOffset? LastSample { get; set; }
        public DateTimeOffset? GapStart { get; set; }
        public bool AwaitingGap { get; set; }
    }
}
