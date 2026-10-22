using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Edge.Bridge.Publishing;

namespace Telemetrix.Edge.Bridge.Tests;

public sealed class StoreAndForwardTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"telemetrix-publish-{Guid.NewGuid():N}");
    private readonly FakeTransport transport = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FailureSpillsNewInputBehindOldAndReturnsToDirectAfterDrain()
    {
        using var buffer = Open();
        using var queue = Queue(buffer);
        transport.Unavailable = true;
        await queue.PublishAsync(Entry(1), Ct);
        await queue.PublishAsync(Entry(2), Ct);
        Assert.Equal(2, buffer.Depth.Batches);
        Assert.Empty(transport.Delivered);
        transport.Unavailable = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var replay = queue.ReplayAsync(stop.Token);
        await WaitAsync(() => buffer.Depth.Batches == 0);
        await queue.PublishAsync(Entry(3), Ct);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);
        Assert.Equal(new byte[] { 1, 2, 3 }, transport.Delivered.Select(entry => entry.Payload[0]));
        Assert.Equal(0, buffer.Depth.Bytes);
    }

    [Fact]
    public async Task FullAdmissionDoesNotBlockReplayAndNeverDropsThePendingEntry()
    {
        using var buffer = Open(2);
        using var queue = Queue(buffer);
        transport.Unavailable = true;
        await queue.PublishAsync(Entry(1), Ct);
        await queue.PublishAsync(Entry(2), Ct);
        var pending = queue.PublishAsync(Entry(3), Ct);
        Assert.False(pending.IsCompleted);
        Assert.Equal(2, buffer.Depth.Bytes);
        transport.Unavailable = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var replay = queue.ReplayAsync(stop.Token);
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await WaitAsync(() => transport.Delivered.Count == 3);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);
        Assert.Equal(new byte[] { 1, 2, 3 }, transport.Delivered.Select(entry => entry.Payload[0]));
    }

    [Fact]
    public async Task RestartReplaysIdenticalPersistedBytesBeforeNewInput()
    {
        using (var initial = Open()) Assert.True(initial.TryAppend(Entry(1)));
        using var buffer = Open();
        using var queue = Queue(buffer);
        await queue.PublishAsync(Entry(2), Ct);
        Assert.Empty(transport.Delivered);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var replay = queue.ReplayAsync(stop.Token);
        await WaitAsync(() => buffer.Depth.Batches == 0);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);
        Assert.Equal(new byte[] { 1, 2 }, transport.Delivered.Select(entry => entry.Payload[0]));
    }

    private SqliteBuffer Open(long maxBytes = 1024) => new(Options.Create(new BufferOptions
    {
        Path = Path.Combine(directory, "bridge.db"), MaxBytes = maxBytes
    }));
    private StoreAndForwardQueue Queue(SqliteBuffer buffer) => new(buffer, transport,
        TimeProvider.System, NullLogger<StoreAndForwardQueue>.Instance);
    private static BufferedEntry Entry(byte value) => new(KafkaOptions.RawTopic, "Pump01",
        [value], 1, DateTimeOffset.UtcNow);
    private async Task WaitAsync(Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
    private sealed class FakeTransport : IKafkaTransport
    {
        public volatile bool Unavailable;
        public ConcurrentQueue<BufferedEntry> Delivered { get; } = new();
        public Task PublishAsync(BufferedEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable) throw new KafkaException(new Error(ErrorCode.Local_Transport));
            Delivered.Enqueue(entry);
            return Task.CompletedTask;
        }
    }
}
