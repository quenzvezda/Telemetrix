namespace Telemetrix.Transformer.Consuming;

public interface IRawBatchHandler
{
    Task HandleAsync(RawBatchDelivery delivery, CancellationToken cancellationToken);
}
