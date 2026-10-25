using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Batching;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources;

namespace Telemetrix.Edge.Bridge.Tests;

public class IngestionCapacityTests
{
    [Fact]
    public async Task ConfiguredBatchQueueAbsorbsAnAssetFlushBurstWhilePublishingWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new BurstSource();
        var publisher = new PausedPublisher();
        var batcher = new AssetBatcher(new BatchingOptions { MaxSamples = 1 }, TimeProvider.System, "mqtt", "mqtt://test");
        using var worker = new OpcUaIngestionWorker(source, batcher, publisher,
            Options.Create(new IngestionOptions { BatchQueueCapacity = 1024 }));
        await worker.StartAsync(ct);
        try
        {
            await source.Collected.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
            publisher.Release.TrySetResult();
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(1700, publisher.Batches);
        }
        finally
        {
            publisher.Release.TrySetResult();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(cleanup.Token);
        }
    }

    private sealed class BurstSource : ISourceAdapter
    {
        public TaskCompletionSource Collected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 1700; i++)
                await writer.WriteAsync(new Sample("Plant01", "Line01", $"Pump{i:D4}", "FlowRate", 50, now, now, 0), cancellationToken);
            Collected.TrySetResult();
        }
    }

    private sealed class PausedPublisher : IBatchPublisher
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Batches { get; private set; }
        public async Task RunAsync(ChannelReader<TelemetryBatch> batches, CancellationToken cancellationToken)
        {
            await Release.Task.WaitAsync(cancellationToken);
            await foreach (var _ in batches.ReadAllAsync(cancellationToken)) Batches++;
        }
    }
}
