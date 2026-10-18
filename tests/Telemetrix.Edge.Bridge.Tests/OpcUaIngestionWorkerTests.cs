using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Sources;

namespace Telemetrix.Edge.Bridge.Tests;

public class OpcUaIngestionWorkerTests
{
    [Fact]
    public async Task SourceFailureTerminatesTheWorkerInsteadOfLeavingTheReaderWaiting()
    {
        var failure = new InvalidOperationException("source failed");
        using var worker = new OpcUaIngestionWorker(new FailingSource(failure),
            NullLogger<OpcUaIngestionWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task StoppingTheWorkerCancelsAndAwaitsTheSource()
    {
        var source = new WaitingSource();
        using var worker = new OpcUaIngestionWorker(source, NullLogger<OpcUaIngestionWorker>.Instance);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(source.Stopped);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    private sealed class FailingSource(Exception failure) : ISourceAdapter
    {
        public Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
            => Task.FromException(failure);
    }

    private sealed class WaitingSource : ISourceAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }

        public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Stopped = true;
            }
        }
    }
}
