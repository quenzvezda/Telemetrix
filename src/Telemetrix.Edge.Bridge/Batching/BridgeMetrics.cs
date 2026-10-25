using System.Diagnostics.Metrics;
using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Edge.Bridge.Batching;

public sealed class BridgeMetrics : IDisposable
{
    public const string MeterName = "Telemetrix.Bridge";
    internal Meter Meter { get; } = new(MeterName);
    private readonly Counter<long> samples;
    private readonly Counter<long> batches;

    public BridgeMetrics(SqliteBuffer buffer)
    {
        samples = Meter.CreateCounter<long>("telemetrix.samples.received", "{sample}");
        batches = Meter.CreateCounter<long>("telemetrix.batches.produced", "{batch}");
        Meter.CreateObservableGauge("telemetrix.buffer.depth", () => buffer.Depth.Batches, "{batch}");
    }

    public void SampleReceived(string asset, string source) => samples.Add(1,
        new KeyValuePair<string, object?>("asset", asset), new KeyValuePair<string, object?>("source", source));
    public void BatchProduced(string asset) => batches.Add(1, new KeyValuePair<string, object?>("asset", asset));
    public void Dispose() => Meter.Dispose();
}
