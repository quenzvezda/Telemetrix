using System.Text.Json;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Tests;

public class TransformationOutputValidatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset[] Inputs = [T0, T0.AddSeconds(1), T0.AddSeconds(2)];
    private static readonly Dictionary<string, OutputTag> Outputs = new() { ["LevelNorm"] = new("1", 0, 1) };

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"samples":[],"dispositions":[]}""")]
    [InlineData("""{"samples":null}""")]
    public void EmptyResultsAreValid(string json)
    {
        var output = Validate(json);

        Assert.Empty(output.Samples);
        Assert.Empty(output.Dispositions);
    }

    [Fact]
    public void ValidResultKeepsOutputsAndDispositions()
    {
        var output = Validate("""
            {"samples":[{"tag":"LevelNorm","value":0.5,"sourceTimestamp":"2026-10-31T15:00:01+07:00","inputs":[1,0],"quality":"uncertain"}],
             "dispositions":[{"index":2,"disposition":"rejected","reason":"stuck sensor"},{"index":0,"disposition":"flagged","reason":"spike"}]}
            """);

        var sample = Assert.Single(output.Samples);
        Assert.Equal(("LevelNorm", 0.5, T0.AddSeconds(1), Quality.Uncertain), (sample.Tag, sample.Value, sample.SourceTimestamp, sample.Quality));
        Assert.Equal([1, 0], sample.Inputs);
        Assert.Equal([new ScriptDisposition(2, "rejected", "stuck sensor"), new ScriptDisposition(0, "flagged", "spike")],
            output.Dispositions);
    }

    [Theory]
    [InlineData("""[]""", "must be an object")]
    [InlineData("""{"sample":[]}""", "unknown key")]
    [InlineData("""{"samples":{}}""", "must be a list")]
    [InlineData("""{"samples":[{"tag":"Level","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}]}""", "not a declared output")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":null,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}]}""", "finite number")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":"1","sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}]}""", "finite number")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":true,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}]}""", "finite number")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1e400,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}]}""", "finite number")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[]}]}""", "at least one input")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[3]}]}""", "input index")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:05Z","inputs":[0]}]}""", "one of its inputs")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"yesterday","inputs":[0]}]}""", "ISO-8601")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0],"quality":"great"}]}""", "good, uncertain or bad")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0],"unit":"%"}]}""", "unknown key")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]},{"tag":"LevelNorm","value":2,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}]}""", "repeats output")]
    [InlineData("""{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]}],"dispositions":[{"index":0,"disposition":"rejected","reason":"bad"}]}""", "rejected input")]
    [InlineData("""{"dispositions":[{"index":0,"disposition":"modified","reason":"x"}]}""", "flagged or rejected")]
    [InlineData("""{"dispositions":[{"index":0,"disposition":"flagged"}]}""", "reason must be")]
    [InlineData("""{"dispositions":[{"index":0,"disposition":"flagged","reason":"a"},{"index":0,"disposition":"rejected","reason":"b"}]}""", "repeats input")]
    [InlineData("""{"dispositions":[{"index":-1,"disposition":"flagged","reason":"a"}]}""", "input index")]
    public void ContractViolationsAreMalformed(string json, string expected)
    {
        var exception = Assert.Throws<MalformedOutputException>(() => Validate(json));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputLimitIsEnforced()
    {
        var json = """{"samples":[{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:00Z","inputs":[0]},{"tag":"LevelNorm","value":1,"sourceTimestamp":"2026-10-31T08:00:01Z","inputs":[1]}]}""";
        using var document = JsonDocument.Parse(json);

        Assert.Contains("More than 1", Assert.Throws<MalformedOutputException>(() =>
            TransformationOutputValidator.Validate(document.RootElement, Inputs, Outputs, 1)).Message, StringComparison.Ordinal);
    }

    private static ValidatedOutput Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return TransformationOutputValidator.Validate(document.RootElement, Inputs, Outputs, 100);
    }
}
