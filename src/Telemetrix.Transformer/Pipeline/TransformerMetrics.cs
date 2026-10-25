using System.Diagnostics.Metrics;

namespace Telemetrix.Transformer.Pipeline;

public sealed class TransformerMetrics : IDisposable
{
    public const string MeterName = "Telemetrix.Transformer";
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> written;
    private readonly Counter<long> deadLettered;
    private readonly Histogram<double> duration;
    private KeyValuePair<int, long>[] lag = [];

    public TransformerMetrics()
    {
        written = meter.CreateCounter<long>("telemetrix.rows.written", "{measurement}");
        deadLettered = meter.CreateCounter<long>("telemetrix.batches.deadlettered", "{batch}");
        duration = meter.CreateHistogram<double>("telemetrix.batch.write.duration", "ms");
        meter.CreateObservableGauge("telemetrix.consumer.lag", () => Volatile.Read(ref lag)
            .Select(partition => new Measurement<long>(partition.Value,
                new KeyValuePair<string, object?>("partition", partition.Key))), "{message}");
    }

    public void WriteCompleted(int inserted, TimeSpan elapsed)
    {
        written.Add(inserted);
        duration.Record(elapsed.TotalMilliseconds);
    }

    public void DeadLetterAcknowledged(string reason) => deadLettered.Add(1,
        new KeyValuePair<string, object?>("reason", reason is "unknown-tag" or "invalid-json" or
            "schema-violation" or "unsupported-schema-version" ? reason : "other"));
    public void UpdateLag(IEnumerable<KeyValuePair<int, long>> partitions) => Volatile.Write(ref lag, partitions.ToArray());
    public void Dispose() => meter.Dispose();
}
