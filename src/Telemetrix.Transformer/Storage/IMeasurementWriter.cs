using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Storage;

public interface IMeasurementWriter
{
    Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken);
}
