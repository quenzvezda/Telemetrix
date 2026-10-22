using Confluent.Kafka;
using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class StoreAndForwardQueue(SqliteBuffer buffer, IKafkaTransport transport,
    TimeProvider timeProvider, ILogger<StoreAndForwardQueue> logger) : IDisposable
{
    private readonly SemaphoreSlim admission = new(1, 1);

    public async Task PublishAsync(BufferedEntry entry, CancellationToken cancellationToken)
    {
        await admission.WaitAsync(cancellationToken);
        try
        {
            if (buffer.Depth.Batches == 0)
            {
                try
                {
                    await transport.PublishAsync(entry, cancellationToken);
                    return;
                }
                catch (KafkaException exception)
                {
                    logger.LogWarning(exception, "Kafka delivery unavailable; retaining accepted input on disk");
                }
            }

            // Replay does not acquire admission: a full queue must still be able to drain.
            while (!buffer.TryAppend(entry))
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken);
        }
        finally
        {
            admission.Release();
        }
    }

    public async Task ReplayAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (buffer.ReadOldest() is not { } oldest)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken);
                continue;
            }
            try
            {
                await transport.PublishAsync(oldest, cancellationToken);
                buffer.DeleteAcknowledged(oldest);
            }
            catch (KafkaException exception)
            {
                logger.LogDebug(exception, "Buffered delivery not acknowledged; retaining sequence {Sequence}", oldest.Sequence);
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
            }
        }
    }

    public void Dispose() => admission.Dispose();
}
