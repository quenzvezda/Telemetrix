using System.Threading.Channels;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Batching;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources;

namespace Telemetrix.Edge.Bridge.Tests;

public class OpcUaIngestionWorkerTests
{
    [Fact]
    public async Task SourceFailureTerminatesTheWorkerInsteadOfLeavingTheReaderWaiting()
    {
        var failure = new InvalidOperationException("source failed");
        using var worker = Worker(new FailingSource(failure), new CollectingPublisher());

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task StoppingTheWorkerCancelsAndAwaitsTheSource()
    {
        var source = new WaitingSource();
        using var worker = Worker(source, new CollectingPublisher());
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(source.Stopped);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StoppingTheWorkerPublishesTheLastOpenBatch()
    {
        var source = new WaitingSource(emitSample: true);
        var publisher = new CollectingPublisher();
        using var worker = Worker(source, publisher);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(source.Stopped);
        var batch = Assert.Single(publisher.Batches);
        Assert.Equal("Pump01", batch.AssetId);
        Assert.Equal(52.8, Assert.Single(batch.Samples).Value);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PublisherFailureCancelsTheSourceAndTerminatesTheWorker()
    {
        var failure = new InvalidOperationException("publisher failed");
        var source = new WaitingSource(emitSample: true);
        using var worker = Worker(source, new FailingPublisher(failure), maxSamples: 1);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.True(source.Stopped);
    }

    private static OpcUaIngestionWorker Worker(ISourceAdapter source, IBatchPublisher publisher,
        int maxSamples = 500) => new(source,
        new AssetBatcher(new BatchingOptions { MaxSamples = maxSamples }, TimeProvider.System,
            "opcua", "opc.tcp://localhost:50000"), publisher);

    private sealed class CollectingPublisher : IBatchPublisher
    {
        public List<TelemetryBatch> Batches { get; } = [];
        public async Task RunAsync(ChannelReader<TelemetryBatch> batches, CancellationToken cancellationToken)
        {
            await foreach (var batch in batches.ReadAllAsync(cancellationToken)) Batches.Add(batch);
        }
    }

    private sealed class FailingPublisher(Exception failure) : IBatchPublisher
    {
        public async Task RunAsync(ChannelReader<TelemetryBatch> batches, CancellationToken cancellationToken)
        {
            await batches.ReadAsync(cancellationToken);
            throw failure;
        }
    }

    private sealed class FailingSource(Exception failure) : ISourceAdapter
    {
        public Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
            => Task.FromException(failure);
    }

    private sealed class WaitingSource(bool emitSample = false) : ISourceAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }

        public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
        {
            try
            {
                if (emitSample)
                {
                    await writer.WriteAsync(new Sample("Plant01", "Line01", "Pump01", "FlowRate", 52.8,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0), cancellationToken);
                }
                Started.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Stopped = true;
            }
        }
    }
}
