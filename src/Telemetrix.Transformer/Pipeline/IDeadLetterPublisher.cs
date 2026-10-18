using Telemetrix.Contracts;

namespace Telemetrix.Transformer.Pipeline;

public interface IDeadLetterPublisher
{
    Task PublishUnknownTagsAsync(TelemetryBatch batch, CancellationToken cancellationToken);
}
