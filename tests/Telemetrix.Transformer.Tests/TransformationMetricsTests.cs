using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer.Tests;

[Collection("Console logging")]
public class TransformationMetricsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private readonly TransformationDirectory directory = new TransformationDirectory()
        .Script("convert.py", "def transform(batch, context):\n    return {}\n")
        .Script("broken.py", "def transform(batch, context):\n    return {}\n")
        .Registry(new
        {
            transformations = new Dictionary<string, object>
            {
                ["convert"] = new { script = "convert.py", outputs = new { TemperatureF = new { unit = "degF" } } },
                ["broken"] = new { script = "broken.py" }
            }
        });
    private readonly ConcurrentBag<(string Name, double Value, Dictionary<string, object?> Tags)> measurements = [];
    private readonly MeterListener listener = new();

    public TransformationMetricsTests()
    {
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TransformerMetrics.MeterName && instrument.Name.StartsWith("telemetrix.transformation.", StringComparison.Ordinal))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();
    }

    [Fact]
    public async Task RunsDurationAndSampleCountsCarryLabelVersionAndOutcome()
    {
        using var metrics = new TransformerMetrics();
        var writer = new Writer();
        var logs = new LogCapture();
        var handler = Handler(metrics, writer, logs, new Runs());
        var version = directory.Load().Find("convert")!.ShortVersion;

        await handler.HandleAsync(Delivery("convert"), TestContext.Current.CancellationToken);
        await handler.HandleAsync(Delivery("broken"), TestContext.Current.CancellationToken);

        var runs = Named("telemetrix.transformation.runs");
        Assert.Contains(runs, run => Equals(run.Tags["label"], "convert") && Equals(run.Tags["version"], version)
            && Equals(run.Tags["outcome"], "succeeded"));
        Assert.Contains(runs, run => Equals(run.Tags["label"], "broken") && Equals(run.Tags["outcome"], "script-error"));
        Assert.Equal(2, Named("telemetrix.transformation.duration").Count);
        Assert.Contains(Named("telemetrix.transformation.samples.in"), sample => Equals(sample.Tags["label"], "convert")
            && Equals(sample.Tags["disposition"], "modified") && sample.Value == 1);
        Assert.Contains(Named("telemetrix.transformation.samples.in"), sample => Equals(sample.Tags["label"], "convert")
            && Equals(sample.Tags["disposition"], "flagged") && sample.Value == 1);
        Assert.Contains(Named("telemetrix.transformation.samples.out"), sample => Equals(sample.Tags["label"], "convert") && sample.Value == 1);
        Assert.Equal(["succeeded", "failed"], writer.Records.Select(record => record!.Status));
        Assert.Contains(logs.Messages, message => message.Contains("Transformation convert sha256:", StringComparison.Ordinal)
            && message.Contains("outcome=succeeded", StringComparison.Ordinal) && message.Contains("flagged=1", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, message => message.Contains("outcome=script-error", StringComparison.Ordinal)
            && message.Contains("ZeroDivisionError", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnregisteredLabelsDoNotCreateNewMetricSeries()
    {
        using var metrics = new TransformerMetrics();
        var handler = Handler(metrics, new Writer(), new LogCapture(), new Runs());

        await handler.HandleAsync(Delivery("made-up-label"), TestContext.Current.CancellationToken);

        var run = Assert.Single(Named("telemetrix.transformation.runs"));
        Assert.Equal(("unregistered", "none", "unknown-label"), (run.Tags["label"], run.Tags["version"], run.Tags["outcome"]));
    }

    [Theory]
    [InlineData(false, TransformationStage.Disabled)]
    [InlineData(true, TransformationStage.AlreadyRecorded)]
    public async Task SkippedBatchesStoreRawSamplesOnlyAndAreCounted(bool enabled, string reason)
    {
        using var metrics = new TransformerMetrics();
        var writer = new Writer();
        var handler = Handler(metrics, writer, new LogCapture(), new Runs { Existing = true }, enabled);

        await handler.HandleAsync(Delivery("convert"), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(writer.Records));
        Assert.Equal(2, writer.Samples);
        Assert.Equal(reason, Assert.Single(Named("telemetrix.transformation.skipped")).Tags["reason"]);
        Assert.Empty(Named("telemetrix.transformation.runs"));
    }

    public void Dispose()
    {
        listener.Dispose();
        directory.Dispose();
    }

    private TelemetryBatchHandler Handler(TransformerMetrics metrics, Writer writer, LogCapture logs, Runs runs, bool enabled = true)
    {
        var options = Options.Create(TransformationDirectory.Options());
        options.Value.Enabled = enabled;
        var engine = new TransformationEngine(directory.Load(), new Runtime(), new DerivedTags(), options, TimeProvider.System);
        var stage = new TransformationStage(options, ResiliencePipeline.Empty, NullLogger<TransformationStage>.Instance,
            metrics, enabled ? engine : null, enabled ? runs : null);
        var resolver = new TagResolver(new Catalog(), Options.Create(new TagResolutionOptions()));
        return new TelemetryBatchHandler(resolver, writer, new DeadLetter(), logs.CreateLogger<TelemetryBatchHandler>(),
            ResiliencePipeline.Empty, stage, metrics);
    }

    private static RawBatchDelivery Delivery(string label) => new(new TelemetryBatch(2, Guid.NewGuid(), "opcua",
        "opc.tcp://test:4840", "Plant01", "Line01", "Tank01", T0,
        [new("Temperature", 25, T0, T0, 0), new("Temperature", 90, T0.AddSeconds(1), T0.AddSeconds(1), 0)], label), "Tank01", 0, 0);

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags) copy[tag.Key] = tag.Value;
        measurements.Add((instrument.Name, value, copy));
    }

    private List<(string Name, double Value, Dictionary<string, object?> Tags)> Named(string name) =>
        measurements.Where(measurement => measurement.Name == name).ToList();

    // convert: output from input 0, flag input 1; broken: a Python-style exception.
    private sealed class Runtime : ITransformationRuntime
    {
        public Task<RuntimeResponse> TransformAsync(RegisteredTransformation transformation, ReadOnlyMemory<byte> batch,
            ReadOnlyMemory<byte> context, CancellationToken cancellationToken) => Task.FromResult(transformation.Label == "broken"
            ? RuntimeResponse.Failed(PythonWorker.ScriptError, "ZeroDivisionError: division by zero")
            : new RuntimeResponse(null, null, JsonDocument.Parse("""
                {"samples":[{"tag":"TemperatureF","value":77,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}],
                 "dispositions":[{"index":1,"disposition":"flagged","reason":"spike"}]}
                """).RootElement.Clone()));
    }

    private sealed class DerivedTags : IDerivedTagCatalog
    {
        public Task<DerivedTagResolution> ResolveAsync(string assetId, string label, string name, OutputTag declared,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DerivedTagResolution(new TagMetadata(42, assetId, name, declared.Unit, null, null), null));
    }

    private sealed class Runs : ITransformationRunStore
    {
        public bool Existing { get; init; }
        public Task<bool> ExistsAsync(Guid batchId, string label, CancellationToken cancellationToken) => Task.FromResult(Existing);
        public Task<bool> SchemaReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class Catalog : ITagCatalog
    {
        public Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagMetadata>>([new(7, "Tank01", "Temperature", "degC", -20, 80)]);
        public Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken) => Task.FromResult<TagMetadata?>(null);
        public Task<TagMetadata?> CreateAsync(string assetId, string tag, CancellationToken cancellationToken) => Task.FromResult<TagMetadata?>(null);
    }

    private sealed class Writer : IMeasurementWriter
    {
        public List<TransformationRecord?> Records { get; } = [];
        public int Samples { get; private set; }
        public Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken) =>
            WriteAsync(batchId, samples, null, cancellationToken);
        public Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, TransformationRecord? transformation,
            CancellationToken cancellationToken)
        {
            Records.Add(transformation);
            Samples = samples.Count;
            return Task.FromResult(samples.Count + (transformation?.OutputCount ?? 0));
        }
    }

    private sealed class DeadLetter : IDeadLetterPublisher
    {
        public Task PublishUnknownTagsAsync(TelemetryBatch batch, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LogCapture : ILoggerFactory, ILogger
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
