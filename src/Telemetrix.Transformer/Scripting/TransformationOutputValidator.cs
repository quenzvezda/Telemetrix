using System.Text.Json;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Scripting;

public sealed record OutputSample(string Tag, double Value, DateTimeOffset SourceTimestamp, IReadOnlyList<int> Inputs,
    Quality? Quality);

public sealed record ScriptDisposition(int Index, string Disposition, string Reason);

public sealed record ValidatedOutput(IReadOnlyList<OutputSample> Samples, IReadOnlyList<ScriptDisposition> Dispositions);

public sealed class MalformedOutputException(string message) : Exception(message);

// Contract version 1 result rules (design.md, "Input/output contract"); nothing is stored from a result that fails.
public static class TransformationOutputValidator
{
    public const string Flagged = "flagged";
    public const string Rejected = "rejected";
    private const int MaxReasonLength = 1024;

    public static ValidatedOutput Validate(JsonElement result, IReadOnlyList<DateTimeOffset> inputTimestamps,
        IReadOnlyDictionary<string, OutputTag> outputs, int maxOutputSamples)
    {
        if (result.ValueKind != JsonValueKind.Object) throw Malformed("The result must be an object.");
        RequireOnly(result, "The result", "samples", "dispositions");
        var dispositions = Dispositions(result, inputTimestamps.Count);
        var rejected = dispositions.Where(item => item.Disposition == Rejected).Select(item => item.Index).ToHashSet();

        var samples = new List<OutputSample>();
        var seen = new HashSet<(string, DateTimeOffset)>();
        foreach (var (item, position) in Array(result, "samples").Select((item, position) => (item, position)))
        {
            if (samples.Count == maxOutputSamples)
                throw Malformed($"More than {maxOutputSamples} output samples.");
            var name = $"samples[{position}]";
            if (item.ValueKind != JsonValueKind.Object) throw Malformed($"{name} must be an object.");
            RequireOnly(item, name, "tag", "value", "sourceTimestamp", "inputs", "quality");

            var tag = item.TryGetProperty("tag", out var tagValue) && tagValue.ValueKind == JsonValueKind.String
                ? tagValue.GetString()! : throw Malformed($"{name}.tag must be a string.");
            if (!outputs.ContainsKey(tag)) throw Malformed($"{name}.tag '{tag}' is not a declared output.");
            if (!item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw Malformed($"{name}.value must be a finite number.");

            if (!item.TryGetProperty("inputs", out var inputsValue) || inputsValue.ValueKind != JsonValueKind.Array
                || inputsValue.GetArrayLength() == 0)
                throw Malformed($"{name}.inputs must list at least one input index.");
            var inputs = new List<int>();
            foreach (var input in inputsValue.EnumerateArray())
            {
                var index = Index(input, inputTimestamps.Count, $"{name}.inputs");
                if (rejected.Contains(index)) throw Malformed($"{name} uses rejected input {index}.");
                inputs.Add(index);
            }

            if (!item.TryGetProperty("sourceTimestamp", out var time) || time.ValueKind != JsonValueKind.String
                || !time.TryGetDateTimeOffset(out var timestamp))
                throw Malformed($"{name}.sourceTimestamp must be an ISO-8601 timestamp.");
            timestamp = timestamp.ToUniversalTime();
            if (!inputs.Any(index => inputTimestamps[index] == timestamp))
                throw Malformed($"{name}.sourceTimestamp must equal the source timestamp of one of its inputs.");
            if (!seen.Add((tag, timestamp)))
                throw Malformed($"{name} repeats output '{tag}' at {timestamp:O}.");

            Quality? quality = null;
            if (item.TryGetProperty("quality", out var qualityValue) && qualityValue.ValueKind != JsonValueKind.Null)
                quality = qualityValue.ValueKind == JsonValueKind.String ? ParseQuality(qualityValue.GetString()!)
                    : throw Malformed($"{name}.quality must be good, uncertain or bad.");
            samples.Add(new OutputSample(tag, number, timestamp, inputs, quality));
        }
        return new ValidatedOutput(samples, dispositions);
    }

    public static string Name(Quality quality) => quality switch
    {
        Quality.Good => "good",
        Quality.Uncertain => "uncertain",
        _ => "bad"
    };

    private static List<ScriptDisposition> Dispositions(JsonElement result, int inputCount)
    {
        var dispositions = new List<ScriptDisposition>();
        var indices = new HashSet<int>();
        foreach (var (item, position) in Array(result, "dispositions").Select((item, position) => (item, position)))
        {
            var name = $"dispositions[{position}]";
            if (item.ValueKind != JsonValueKind.Object) throw Malformed($"{name} must be an object.");
            RequireOnly(item, name, "index", "disposition", "reason");
            var index = item.TryGetProperty("index", out var indexValue)
                ? Index(indexValue, inputCount, $"{name}.index") : throw Malformed($"{name}.index is required.");
            if (!indices.Add(index)) throw Malformed($"{name} repeats input {index}.");
            var disposition = item.TryGetProperty("disposition", out var kind) && kind.ValueKind == JsonValueKind.String
                ? kind.GetString() : null;
            if (disposition is not (Flagged or Rejected)) throw Malformed($"{name}.disposition must be flagged or rejected.");
            var reason = item.TryGetProperty("reason", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() : null;
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength)
                throw Malformed($"{name}.reason must be a non-empty string of at most {MaxReasonLength} characters.");
            dispositions.Add(new ScriptDisposition(index, disposition, reason));
        }
        return dispositions;
    }

    private static IEnumerable<JsonElement> Array(JsonElement result, string property) =>
        !result.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null ? []
        : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray()
        : throw Malformed($"\"{property}\" must be a list.");

    private static int Index(JsonElement value, int count, string name) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index >= 0 && index < count
            ? index : throw Malformed($"{name} must be an input index between 0 and {count - 1}.");

    private static Quality ParseQuality(string name) => name switch
    {
        "good" => Quality.Good,
        "uncertain" => Quality.Uncertain,
        "bad" => Quality.Bad,
        _ => throw Malformed($"Quality '{name}' must be good, uncertain or bad.")
    };

    private static void RequireOnly(JsonElement element, string name, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Malformed($"{name} has unknown key \"{property.Name}\".");
    }

    private static MalformedOutputException Malformed(string message) => new(message);
}
