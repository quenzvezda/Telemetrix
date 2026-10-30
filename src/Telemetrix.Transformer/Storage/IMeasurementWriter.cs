using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Storage;

public interface IMeasurementWriter
{
    Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken);

    // Stores the transformation record with the Batch's Measurements in one transaction (ADR-0018).
    Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, TransformationRecord? transformation,
        CancellationToken cancellationToken) => transformation is null
        ? WriteAsync(batchId, samples, cancellationToken)
        : throw new NotSupportedException("This writer cannot store transformation records.");
}
