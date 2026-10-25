using System.Threading.Channels;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Batching;

public sealed class AssetBatcher
{
    private readonly BatchingOptions options;
    private readonly TimeProvider timeProvider;
    private readonly string source;
    private readonly string endpoint;
    private readonly BridgeMetrics? metrics;

    public AssetBatcher(BatchingOptions options, TimeProvider timeProvider, string source, string endpoint,
        BridgeMetrics? metrics = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxSamples);
        if (options.MaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Batching:MaxAge must be positive.");
        }

        this.options = options;
        this.timeProvider = timeProvider;
        this.source = source;
        this.endpoint = endpoint;
        this.metrics = metrics;
    }

    // Completing the input is graceful shutdown; cancellation aborts a blocked pipeline.
    public async Task RunAsync(ChannelReader<Sample> input, ChannelWriter<TelemetryBatch> output,
        CancellationToken cancellationToken)
    {
        var open = new Dictionary<string, OpenBatch>(StringComparer.Ordinal);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        Exception? failure = null;
        try
        {
            var readable = input.WaitToReadAsync(lifetime.Token).AsTask();
            var tick = timer.WaitForNextTickAsync(lifetime.Token).AsTask();
            while (true)
            {
                await Task.WhenAny(readable, tick);
                if (readable.IsCompleted)
                {
                    if (!await readable)
                    {
                        break;
                    }

                    // Bound each read turn so a continuously busy source cannot starve the timer.
                    for (var count = 0; count < 256 && input.TryRead(out var sample); count++)
                    {
                        metrics?.SampleReceived(sample.Asset, source);
                        if (!open.TryGetValue(sample.Asset, out var batch))
                        {
                            batch = new OpenBatch(sample.Plant, sample.Line, timeProvider.GetTimestamp());
                            open.Add(sample.Asset, batch);
                        }

                        batch.Samples.Add(new BatchSample(sample.Tag, sample.Value,
                            sample.SourceTimestamp, sample.ServerTimestamp, sample.StatusCode));
                        if (batch.Samples.Count >= options.MaxSamples)
                        {
                            await FlushAsync(sample.Asset, batch);
                        }
                    }

                    readable = input.WaitToReadAsync(lifetime.Token).AsTask();
                }

                if (tick.IsCompleted)
                {
                    if (!await tick)
                    {
                        break;
                    }

                    foreach (var (asset, batch) in open.ToArray())
                    {
                        if (timeProvider.GetElapsedTime(batch.Started) >= options.MaxAge)
                        {
                            await FlushAsync(asset, batch);
                        }
                    }

                    tick = timer.WaitForNextTickAsync(lifetime.Token).AsTask();
                }
            }

            foreach (var (asset, batch) in open.ToArray())
            {
                await FlushAsync(asset, batch);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await lifetime.CancelAsync();
            output.TryComplete(failure);
        }

        async Task FlushAsync(string asset, OpenBatch batch)
        {
            var now = timeProvider.GetUtcNow();
            var message = new TelemetryBatch(TelemetryBatch.CurrentSchemaVersion,
                Guid.CreateVersion7(now), source, endpoint, batch.Plant, batch.Line, asset, now, batch.Samples);
            await output.WriteAsync(message, lifetime.Token);
            open.Remove(asset);
        }
    }

    private sealed class OpenBatch(string plant, string line, long started)
    {
        public string Plant { get; } = plant;
        public string Line { get; } = line;
        public long Started { get; } = started;
        public List<BatchSample> Samples { get; } = [];
    }
}
