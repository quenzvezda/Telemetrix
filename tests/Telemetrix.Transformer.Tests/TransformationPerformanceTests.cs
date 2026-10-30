using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Tests;

// Per-Batch cost of the Python runtime at the D5 batch size, recorded in docs/m10-review.md (NFR-06, NFR-07).
[Collection("Console logging")]
public class TransformationPerformanceTests
{
    private const int Samples = 500;
    private const int Runs = 200;
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("celsius-to-fahrenheit", "Tank01")]
    [InlineData("pump-cleaning", "Pump01")]
    [InlineData("pump-outliers", "Pump01")]
    public async Task FiveHundredSampleBatchesStayFarBelowTheLatencyBudget(string label, string asset)
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = TransformationRegistry.Load(Path.Combine(AppContext.BaseDirectory, "transformations"));
        var options = Options.Create(TransformationDirectory.Options());
        using var worker = new PythonWorker(options, registry, NullLogger<PythonWorker>.Instance);
        await worker.StartAsync(ct);
        var engine = new TransformationEngine(registry, worker, new DerivedTags(), options, TimeProvider.System);
        var (batch, inputs, validated) = Batch(label, asset);

        for (var warmup = 0; warmup < 20; warmup++) await engine.TransformAsync(batch, label, inputs, validated, ct);
        var elapsed = new double[Runs];
        for (var run = 0; run < Runs; run++)
        {
            var started = Stopwatch.GetTimestamp();
            var record = await engine.TransformAsync(batch, label, inputs, validated, ct);
            elapsed[run] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(TransformationRecord.Succeeded, record.Status);
        }

        Array.Sort(elapsed);
        var p50 = elapsed[Runs / 2];
        var p95 = elapsed[(int)(Runs * 0.95) - 1];
        TestContext.Current.TestOutputHelper?.WriteLine($"M10 transformation cost {label}: samples={Samples} runs={Runs} p50={p50:F3} ms p95={p95:F3} ms max={elapsed[^1]:F3} ms");
        // 5,000 samples/s at 500 per Batch is 10 Batches/s; well under 100 ms per Batch keeps the consumer ahead (NFR-07).
        Assert.InRange(p95, 0, 100);
    }

    private static (TelemetryBatch, ResolvedSample[], ValidatedSample[]) Batch(string label, string asset)
    {
        TagMetadata[] tags = asset == "Tank01"
            ? [new(6, "Tank01", "Level", "%", 0, 100), new(7, "Tank01", "Temperature", "degC", -20, 80)]
            : [new(2, "Pump01", "FlowRate", "m3/h", 0, 80), new(3, "Pump01", "DischargePressure", "bar", 0, 8),
               new(4, "Pump01", "MotorTemperature", "degC", -20, 120), new(5, "Pump01", "Vibration", "mm/s", 0, 20)];
        var inputs = Enumerable.Range(0, Samples).Select(index =>
        {
            var tag = tags[index % tags.Length];
            var value = (tag.MinValid!.Value + tag.MaxValid!.Value) / 2 + Math.Sin(index) * 2;
            var time = T0.AddMilliseconds(index * 10);
            return new ResolvedSample(new BatchSample(tag.Name, value, time, time, 0), tag);
        }).ToArray();
        var validated = inputs.Select(input => new ValidatedSample(input.Tag.TagId, input.Sample.SourceTimestamp,
            input.Sample.Value, Quality.Good)).ToArray();
        return (new TelemetryBatch(2, Guid.NewGuid(), "opcua", "opc.tcp://plantsim:4840", "Plant01", "Line01", asset, T0,
            inputs.Select(input => input.Sample).ToArray(), label), inputs, validated);
    }

    private sealed class DerivedTags : IDerivedTagCatalog
    {
        public Task<DerivedTagResolution> ResolveAsync(string assetId, string label, string name, OutputTag declared,
            CancellationToken cancellationToken) => Task.FromResult(new DerivedTagResolution(
            new TagMetadata(100, assetId, name, declared.Unit, declared.MinValid, declared.MaxValid), null));
    }
}
