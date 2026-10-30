using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Tests;

public class TransformationEngineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly TagMetadata Temperature = new(7, "Tank01", "Temperature", "degC", -20, 80);
    private static readonly TagMetadata TemperatureF = new(42, "Tank01", "TemperatureF", "degF", -4, 176);
    private readonly TransformationDirectory directory = new TransformationDirectory().Single("convert",
        "def transform(batch, context):\n    return {}\n",
        new { TemperatureF = new { unit = "degF", minValid = -4, maxValid = 176 } }, new { factor = 1.8 });
    private readonly FakeTimeProvider time = new(T0);

    [Fact]
    public async Task ScriptReceivesValidatedKnownSamplesAndTheContext()
    {
        var runtime = new Runtime("{}");

        var record = await Engine(runtime).TransformAsync(Batch(), "convert", Inputs(), Validated(), TestContext.Current.CancellationToken);

        Assert.Equal(TransformationRecord.Succeeded, record.Status);
        var batch = JsonNode.Parse(runtime.Batch)!;
        Assert.Equal("Tank01", (string?)batch["assetId"]);
        var samples = batch["samples"]!.AsArray();
        Assert.Equal(3, samples.Count);
        Assert.Equal("""{"index":2,"tag":"Temperature","value":500,"sourceTimestamp":"2026-10-31T08:00:02Z","serverTimestamp":"2026-10-31T08:00:02Z","statusCode":1073741824,"quality":"bad","unit":"degC","minValid":-20,"maxValid":80}""",
            samples[2]!.ToJsonString());
        var context = JsonNode.Parse(runtime.Context)!;
        Assert.Equal(1, (int?)context["contractVersion"]);
        Assert.Equal("convert", (string?)context["label"]);
        Assert.StartsWith("sha256:", (string?)context["version"], StringComparison.Ordinal);
        Assert.Equal(1.8, (double?)context["parameters"]!["factor"]);
        Assert.Equal("degF", (string?)context["outputs"]!["TemperatureF"]!["unit"]);
        Assert.Equal((3, 3, 0, 0, 0, 0), (record.InputCount, record.UnchangedCount, record.ModifiedCount,
            record.FlaggedCount, record.RejectedCount, record.OutputCount));
    }

    [Fact]
    public async Task DispositionsAndDerivedQualityFollowTheContract()
    {
        var runtime = new Runtime("""
            {"samples":[
              {"tag":"TemperatureF","value":77,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0],"quality":"good"},
              {"tag":"TemperatureF","value":200,"sourceTimestamp":"2026-10-31T08:00:01Z","inputs":[1]}],
             "dispositions":[{"index":1,"disposition":"flagged","reason":"spike"},
                             {"index":2,"disposition":"rejected","reason":"out of range"}]}
            """);

        var record = await Engine(runtime).TransformAsync(Batch(), "convert", Inputs(), Validated(), TestContext.Current.CancellationToken);

        Assert.Equal((3, 0, 1, 1, 1, 2), (record.InputCount, record.UnchangedCount, record.ModifiedCount,
            record.FlaggedCount, record.RejectedCount, record.OutputCount));
        Assert.Equal([
            new ValidatedSample(42, T0, 77, Quality.Good),
            // Flagged input lowers to Uncertain, then the derived range (-4…176 degF) forces Bad.
            new ValidatedSample(42, T0.AddSeconds(1), 200, Quality.Bad)], record.Derived);
        Assert.Equal([
            new SampleDisposition(1, 7, T0.AddSeconds(1), "flagged", "spike"),
            new SampleDisposition(2, 7, T0.AddSeconds(2), "rejected", "out of range")], record.Dispositions);
    }

    [Fact]
    public async Task ScriptsCannotRaiseQualityAndFlaggedInputsAreUncertain()
    {
        var runtime = new Runtime("""
            {"samples":[{"tag":"TemperatureF","value":80,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0,1],"quality":"good"}],
             "dispositions":[{"index":0,"disposition":"flagged","reason":"drift"}]}
            """);
        var validated = Validated();
        validated[1] = validated[1] with { Quality = Quality.Good };

        var record = await Engine(runtime).TransformAsync(Batch(), "convert", Inputs(), validated, TestContext.Current.CancellationToken);

        Assert.Equal(Quality.Uncertain, Assert.Single(record.Derived).Quality);
        Assert.Equal((1, 1), (record.FlaggedCount, record.ModifiedCount));
    }

    [Theory]
    [InlineData("missing", TransformationRecord.UnknownLabel)]
    [InlineData("script-error", PythonWorker.ScriptError)]
    [InlineData("timeout", PythonWorker.Timeout)]
    [InlineData("malformed", PythonWorker.MalformedOutput)]
    [InlineData("conflict", TransformationRecord.OutputTagConflict)]
    public async Task PermanentFailuresAreRecordedWithAllInputsUnchanged(string kind, string reason)
    {
        var runtime = kind switch
        {
            "script-error" => new Runtime(null, PythonWorker.ScriptError, "ZeroDivisionError"),
            "timeout" => new Runtime(null, PythonWorker.Timeout, "killed"),
            "malformed" => new Runtime("""{"samples":[{"tag":"TemperatureF","value":1,"sourceTimestamp":"2026-10-31T09:00:00Z","inputs":[0]}]}"""),
            _ => new Runtime("""{"samples":[{"tag":"TemperatureF","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}]}""")
        };
        var catalog = new Catalog { Conflict = kind == "conflict" };

        var record = await Engine(runtime, catalog).TransformAsync(Batch(), kind == "missing" ? "unregistered" : "convert",
            Inputs(), Validated(), TestContext.Current.CancellationToken);

        Assert.Equal((TransformationRecord.Failed, reason), (record.Status, record.Reason));
        Assert.False(string.IsNullOrEmpty(record.Error));
        Assert.Equal((3, 3, 0, 0, 0, 0), (record.InputCount, record.UnchangedCount, record.ModifiedCount,
            record.FlaggedCount, record.RejectedCount, record.OutputCount));
        Assert.Empty(record.Dispositions);
        Assert.Equal(kind == "missing", record.ScriptVersion is null);
    }

    [Fact]
    public async Task UnavailableRuntimePropagatesSoTheOffsetIsHeld()
    {
        var runtime = new Runtime(null) { Unavailable = true };

        await Assert.ThrowsAsync<TransformationRuntimeException>(() => Engine(runtime).TransformAsync(
            Batch(), "convert", Inputs(), Validated(), TestContext.Current.CancellationToken));
    }

    public void Dispose() => directory.Dispose();

    private TransformationEngine Engine(Runtime runtime, Catalog? catalog = null) => new(directory.Load(), runtime,
        catalog ?? new Catalog(), Options.Create(TransformationDirectory.Options()), time);

    private static TelemetryBatch Batch() => new(2, Guid.NewGuid(), "opcua", "opc.tcp://plantsim:4840", "Plant01",
        "Line01", "Tank01", T0, Inputs().Select(input => input.Sample).ToArray(), "convert");

    private static ResolvedSample[] Inputs() =>
    [
        new(new BatchSample("Temperature", 25, T0, T0, 0), Temperature),
        new(new BatchSample("Temperature", 93.3, T0.AddSeconds(1), T0.AddSeconds(1), 0x40000000), Temperature),
        new(new BatchSample("Temperature", 500, T0.AddSeconds(2), T0.AddSeconds(2), 0x40000000), Temperature)
    ];

    private static ValidatedSample[] Validated() =>
    [
        new(7, T0, 25, Quality.Good),
        new(7, T0.AddSeconds(1), 93.3, Quality.Bad),
        new(7, T0.AddSeconds(2), 500, Quality.Bad)
    ];

    private sealed class Runtime(string? result, string? reason = null, string? message = null) : ITransformationRuntime
    {
        public bool Unavailable { get; init; }
        public string Batch { get; private set; } = "";
        public string Context { get; private set; } = "";

        public Task<RuntimeResponse> TransformAsync(RegisteredTransformation transformation, ReadOnlyMemory<byte> batch,
            ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            if (Unavailable) throw new TransformationRuntimeException("python missing");
            Batch = System.Text.Encoding.UTF8.GetString(batch.Span);
            Context = System.Text.Encoding.UTF8.GetString(context.Span);
            return Task.FromResult(reason is not null ? RuntimeResponse.Failed(reason, message!)
                : new RuntimeResponse(null, null, JsonDocument.Parse(result!).RootElement.Clone()));
        }
    }

    private sealed class Catalog : IDerivedTagCatalog
    {
        public bool Conflict { get; init; }
        public Task<DerivedTagResolution> ResolveAsync(string assetId, string label, string name, OutputTag declared,
            CancellationToken cancellationToken) => Task.FromResult(Conflict
            ? new DerivedTagResolution(null, $"Tag '{name}' is a raw Tag.")
            : new DerivedTagResolution(TemperatureF, null));
    }
}
