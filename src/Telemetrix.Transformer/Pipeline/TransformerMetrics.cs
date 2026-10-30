using System.Diagnostics.Metrics;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Pipeline;

public sealed class TransformerMetrics : IDisposable
{
    public const string MeterName = "Telemetrix.Transformer";
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> written;
    private readonly Counter<long> deadLettered;
    private readonly Histogram<double> duration;
    private readonly Counter<long> transformationRuns;
    private readonly Histogram<double> transformationDuration;
    private readonly Counter<long> transformationInputs;
    private readonly Counter<long> transformationOutputs;
    private readonly Counter<long> transformationSkips;
    private KeyValuePair<int, long>[] lag = [];

    public TransformerMetrics()
    {
        written = meter.CreateCounter<long>("telemetrix.rows.written", "{measurement}");
        deadLettered = meter.CreateCounter<long>("telemetrix.batches.deadlettered", "{batch}");
        duration = meter.CreateHistogram<double>("telemetrix.batch.write.duration", "ms");
        transformationRuns = meter.CreateCounter<long>("telemetrix.transformation.runs", "{run}");
        transformationDuration = meter.CreateHistogram<double>("telemetrix.transformation.duration", "ms");
        transformationInputs = meter.CreateCounter<long>("telemetrix.transformation.samples.in", "{sample}");
        transformationOutputs = meter.CreateCounter<long>("telemetrix.transformation.samples.out", "{sample}");
        transformationSkips = meter.CreateCounter<long>("telemetrix.transformation.skipped", "{batch}");
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
    // Unregistered labels share one value so a Batch cannot create unbounded metric series.
    public void TransformationCompleted(TransformationRecord record)
    {
        var label = new KeyValuePair<string, object?>("label", record.ScriptVersion is null ? "unregistered" : record.Label);
        var version = new KeyValuePair<string, object?>("version", record.ScriptVersion is { } script ? script["sha256:".Length..][..12] : "none");
        var outcome = new KeyValuePair<string, object?>("outcome", record.Outcome);
        transformationRuns.Add(1, label, version, outcome);
        transformationDuration.Record(record.DurationMs, label, version, outcome);
        foreach (var (disposition, count) in new[] { ("unchanged", record.UnchangedCount), ("modified", record.ModifiedCount),
                     ("flagged", record.FlaggedCount), ("rejected", record.RejectedCount) })
            if (count > 0) transformationInputs.Add(count, label, new KeyValuePair<string, object?>("disposition", disposition));
        if (record.OutputCount > 0) transformationOutputs.Add(record.OutputCount, label);
    }

    public void TransformationSkipped(string reason) =>
        transformationSkips.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void UpdateLag(IEnumerable<KeyValuePair<int, long>> partitions) => Volatile.Write(ref lag, partitions.ToArray());
    public void Dispose() => meter.Dispose();
}
