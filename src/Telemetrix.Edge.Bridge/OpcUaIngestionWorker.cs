using System.Threading.Channels;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Batching;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources;

namespace Telemetrix.Edge.Bridge;

public sealed class OpcUaIngestionWorker(ISourceAdapter source, AssetBatcher batcher,
    IBatchPublisher publisher) : BackgroundService
{
    private readonly CancellationTokenSource forceStop = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var samples = Channel.CreateBounded<Sample>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var batches = Channel.CreateBounded<TelemetryBatch>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var pipelineAbort = CancellationTokenSource.CreateLinkedTokenSource(forceStop.Token);
        using var sourceStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, pipelineAbort.Token);
        var collecting = CollectAsync(samples.Writer, sourceStop.Token);
        var batching = batcher.RunAsync(samples.Reader, batches.Writer, pipelineAbort.Token);
        var publishing = publisher.RunAsync(batches.Reader, pipelineAbort.Token);
        var stages = new List<Task> { collecting, batching, publishing };
        try
        {
            while (stages.Count > 0)
            {
                var completed = await Task.WhenAny(stages);
                await completed;
                stages.Remove(completed);
            }
        }
        catch
        {
            await pipelineAbort.CancelAsync();
            await sourceStop.CancelAsync();
            try
            {
                await Task.WhenAll(collecting, batching, publishing);
            }
            catch
            {
                // Preserve the first failing stage after observing every stage's completion.
            }
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the source first; only the host shutdown deadline aborts the drain.
        using var deadline = cancellationToken.Register(() => forceStop.Cancel());
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        forceStop.Cancel();
        base.Dispose();
        forceStop.Dispose();
    }

    private async Task CollectAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await source.RunAsync(writer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }
}
