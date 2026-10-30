using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Tests;

// The shipped deploy/transformations examples, run through the real Python runner.
public class TransformationExamplesTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "transformations");
    private static readonly Dictionary<string, TagMetadata> Raw = new[]
    {
        new TagMetadata(2, "Pump01", "FlowRate", "m3/h", 0, 80),
        new TagMetadata(3, "Pump01", "DischargePressure", "bar", 0, 8),
        new TagMetadata(4, "Pump01", "MotorTemperature", "degC", -20, 120),
        new TagMetadata(6, "Tank01", "Level", "%", 0, 100),
        new TagMetadata(7, "Tank01", "Temperature", "degC", -20, 80)
    }.ToDictionary(tag => tag.Name);
    private readonly TransformationRegistry registry = TransformationRegistry.Load(Directory);
    private readonly PythonWorker worker;
    private readonly DerivedTags derived = new();

    public TransformationExamplesTests() => worker = new PythonWorker(Options.Create(TransformationDirectory.Options()),
        registry, NullLogger<PythonWorker>.Instance);

    [Fact]
    public async Task AllExamplesLoad()
    {
        await worker.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["celsius-to-fahrenheit", "pump-cleaning", "pump-outliers", "tank-normalization"],
            registry.All.Select(entry => entry.Label).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OutlierDetectionFlagsOnlyTheSpike()
    {
        var record = await RunAsync("pump-outliers", "Pump01",
            [.. new[] { 52.1, 52.8, 51.9, 52.4, 75.0, 52.6 }.Select(value => ("FlowRate", (double?)value, 0u))]);

        Assert.Equal((TransformationRecord.Succeeded, 5, 1, 0), (record.Status, record.UnchangedCount, record.FlaggedCount, record.OutputCount));
        var flagged = Assert.Single(record.Dispositions);
        Assert.Equal((4, "flagged"), (flagged.Index, flagged.Disposition));
        Assert.Contains("FlowRate modified z-score", flagged.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataCleaningRejectsBadSamplesAndHoldsSpikes()
    {
        var record = await RunAsync("pump-cleaning", "Pump01",
        [
            ("FlowRate", 50, 0), ("FlowRate", 52, 0), ("FlowRate", 79, 0), ("FlowRate", null, 0),
            ("FlowRate", 120, 0), ("FlowRate", 53, 0), ("DischargePressure", 3.2, 0x40000000), ("MotorTemperature", 60, 0)
        ]);

        Assert.Equal((TransformationRecord.Succeeded, 1, 4, 1, 2), (record.Status, record.UnchangedCount,
            record.ModifiedCount, record.FlaggedCount, record.RejectedCount));
        Assert.Equal(new double?[] { 50, 52, 52, 53, 3.2 }, record.Derived.Select(sample => sample.Value));
        Assert.Equal([Quality.Good, Quality.Good, Quality.Uncertain, Quality.Good, Quality.Uncertain],
            record.Derived.Select(sample => sample.Quality));
        Assert.Equal([T0, T0.AddSeconds(1), T0.AddSeconds(2), T0.AddSeconds(5), T0.AddSeconds(6)],
            record.Derived.Select(sample => sample.SourceTimestamp));
        Assert.Equal(["flagged", "rejected", "rejected"], record.Dispositions.Select(item => item.Disposition));
        Assert.Equal([2, 3, 4], record.Dispositions.Select(item => item.Index));
    }

    [Fact]
    public async Task CelsiusToFahrenheitUsesTheDerivedTagUnitAndRange()
    {
        var record = await RunAsync("celsius-to-fahrenheit", "Tank01",
            [("Temperature", 25, 0), ("Temperature", 79, 0), ("Temperature", 100, 0), ("Level", 40, 0)]);

        Assert.Equal((4, 3, 3, 1), (record.InputCount, record.ModifiedCount, record.OutputCount, record.UnchangedCount));
        Assert.Equal(new double?[] { 77, 174.2, 212 }, record.Derived.Select(sample => sample.Value));
        // 79 degC is valid raw and 174.2 degF is valid derived; 100 degC is Bad raw and Bad derived (above 176 degF).
        Assert.Equal([Quality.Good, Quality.Good, Quality.Bad], record.Derived.Select(sample => sample.Quality));
        Assert.All(record.Derived, sample => Assert.Equal(derived.Find("TemperatureF").TagId, sample.TagId));
        Assert.Equal("degF", derived.Find("TemperatureF").Unit);
    }

    [Fact]
    public async Task NormalizationScalesFromStaticParameters()
    {
        var record = await RunAsync("tank-normalization", "Tank01",
            [("Level", 0, 0), ("Level", 25, 0), ("Level", 100, 0), ("Temperature", 30, 0), ("Level", null, 0)]);

        Assert.Equal(new double?[] { 0, 0.25, 1, 0.5 }, record.Derived.Select(sample => sample.Value));
        Assert.Equal(["LevelNorm", "LevelNorm", "LevelNorm", "TemperatureNorm"],
            record.Derived.Select(sample => derived.Name(sample.TagId)));
        Assert.Equal((4, 1), (record.ModifiedCount, record.UnchangedCount));
    }

    public void Dispose() => worker.Dispose();

    private async Task<TransformationRecord> RunAsync(string label, string asset, (string Tag, double? Value, uint Status)[] samples)
    {
        var inputs = samples.Select((sample, index) => new ResolvedSample(new BatchSample(sample.Tag, sample.Value,
            T0.AddSeconds(index), T0.AddSeconds(index), sample.Status), Raw[sample.Tag])).ToArray();
        var validated = inputs.Select(input => new ValidatedSample(input.Tag.TagId, input.Sample.SourceTimestamp,
            input.Sample.Value, RangeCheck.Evaluate(input.Sample.Value, QualityMapper.Map(input.Sample.StatusCode),
                input.Tag.MinValid, input.Tag.MaxValid))).ToArray();
        var batch = new TelemetryBatch(2, Guid.NewGuid(), "opcua", "opc.tcp://plantsim:4840", "Plant01", "Line01", asset, T0,
            inputs.Select(input => input.Sample).ToArray(), label);
        var engine = new TransformationEngine(registry, worker, derived, Options.Create(TransformationDirectory.Options()),
            TimeProvider.System);
        return await engine.TransformAsync(batch, label, inputs, validated, TestContext.Current.CancellationToken);
    }

    private sealed class DerivedTags : IDerivedTagCatalog
    {
        private readonly Dictionary<string, TagMetadata> tags = [];
        public TagMetadata Find(string name) => tags[name];
        public string Name(int tagId) => tags.Values.Single(tag => tag.TagId == tagId).Name;

        public Task<DerivedTagResolution> ResolveAsync(string assetId, string label, string name, OutputTag declared,
            CancellationToken cancellationToken)
        {
            if (!tags.TryGetValue(name, out var tag))
                tags[name] = tag = new TagMetadata(100 + tags.Count, assetId, name, declared.Unit, declared.MinValid, declared.MaxValid);
            return Task.FromResult(new DerivedTagResolution(tag, null));
        }
    }
}
