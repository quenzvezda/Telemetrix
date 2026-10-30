using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Scripting;

public sealed record DerivedTagResolution(TagMetadata? Tag, string? Conflict);

public interface IDerivedTagCatalog
{
    // Creates the derived Tag on first use; a raw Tag, another label's Tag or another unit is a conflict.
    Task<DerivedTagResolution> ResolveAsync(string assetId, string label, string name, OutputTag declared,
        CancellationToken cancellationToken);
}

// Runs one labeled Batch through its script and turns the result into a durable record (ADR-0018).
public sealed class TransformationEngine(TransformationRegistry registry, ITransformationRuntime runtime,
    IDerivedTagCatalog derivedTags, IOptions<TransformationOptions> options, TimeProvider timeProvider)
{
    private const int MaxErrorLength = 1024;

    public async Task<TransformationRecord> TransformAsync(TelemetryBatch batch, string label,
        IReadOnlyList<ResolvedSample> inputs, IReadOnlyList<ValidatedSample> validated, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var transformation = registry.Find(label);
        if (transformation is null)
            return Failed(TransformationRecord.UnknownLabel, $"Transformation '{label}' is not registered.");

        var response = await runtime.TransformAsync(transformation, BatchJson(batch, inputs, validated),
            ContextJson(transformation), cancellationToken);
        if (response.FailureReason is { } reason) return Failed(reason, response.Message ?? "");

        ValidatedOutput output;
        try
        {
            output = TransformationOutputValidator.Validate(response.Result,
                inputs.Select(input => input.Sample.SourceTimestamp).ToArray(), transformation.Outputs,
                options.Value.MaxOutputSamples);
        }
        catch (MalformedOutputException exception)
        {
            return Failed(PythonWorker.MalformedOutput, exception.Message);
        }

        var tags = new Dictionary<string, TagMetadata>(StringComparer.Ordinal);
        foreach (var name in output.Samples.Select(sample => sample.Tag).Distinct(StringComparer.Ordinal))
        {
            var resolution = await derivedTags.ResolveAsync(batch.AssetId, label, name, transformation.Outputs[name],
                cancellationToken);
            if (resolution.Tag is null)
                return Failed(TransformationRecord.OutputTagConflict, resolution.Conflict ?? $"Output Tag '{name}' is unavailable.");
            tags.Add(name, resolution.Tag);
        }

        var dispositions = output.Dispositions.ToDictionary(item => item.Index);
        var referenced = output.Samples.SelectMany(sample => sample.Inputs).ToHashSet();
        var derived = output.Samples.Select(sample =>
        {
            var tag = tags[sample.Tag];
            // Lower only: inputs, a flagged input and the script's own Quality, then the derived Tag's range.
            var quality = sample.Inputs.Max(index => validated[index].Quality);
            if (sample.Inputs.Any(index => dispositions.ContainsKey(index))) quality = Max(quality, Quality.Uncertain);
            if (sample.Quality is { } declared) quality = Max(quality, declared);
            return new ValidatedSample(tag.TagId, sample.SourceTimestamp, sample.Value,
                RangeCheck.Evaluate(sample.Value, quality, tag.MinValid, tag.MaxValid));
        }).ToArray();
        var stored = output.Dispositions.Select(item => new SampleDisposition(item.Index, inputs[item.Index].Tag.TagId,
            inputs[item.Index].Sample.SourceTimestamp, item.Disposition, item.Reason)).ToArray();

        var flagged = output.Dispositions.Count(item => item.Disposition == TransformationOutputValidator.Flagged);
        var rejected = output.Dispositions.Count - flagged;
        var modified = referenced.Count(index => !dispositions.ContainsKey(index));
        return new TransformationRecord(label, batch.AssetId, transformation.Version, TransformationRecord.Succeeded,
            null, null, inputs.Count, inputs.Count - modified - flagged - rejected, modified, flagged, rejected,
            derived, stored, Elapsed());

        TransformationRecord Failed(string reason, string error) => new(label, batch.AssetId, transformation?.Version,
            TransformationRecord.Failed, reason, error.Length > MaxErrorLength ? error[^MaxErrorLength..] : error,
            inputs.Count, inputs.Count, 0, 0, 0, [], [], Elapsed());

        double Elapsed() => timeProvider.GetElapsedTime(started).TotalMilliseconds;
    }

    private static Quality Max(Quality left, Quality right) => left >= right ? left : right;

    private static byte[] BatchJson(TelemetryBatch batch, IReadOnlyList<ResolvedSample> inputs,
        IReadOnlyList<ValidatedSample> validated)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("batchId", batch.BatchId);
            writer.WriteNumber("schemaVersion", batch.SchemaVersion);
            writer.WriteString("source", batch.Source);
            writer.WriteString("endpoint", batch.Endpoint);
            writer.WriteString("plantId", batch.PlantId);
            writer.WriteString("lineId", batch.LineId);
            writer.WriteString("assetId", batch.AssetId);
            writer.WriteString("createdAt", batch.CreatedAt.UtcDateTime);
            writer.WriteStartArray("samples");
            for (var index = 0; index < inputs.Count; index++)
            {
                var (sample, tag) = (inputs[index].Sample, inputs[index].Tag);
                writer.WriteStartObject();
                writer.WriteNumber("index", index);
                writer.WriteString("tag", sample.Tag);
                if (sample.Value is { } value && double.IsFinite(value)) writer.WriteNumber("value", value);
                else writer.WriteNull("value");
                writer.WriteString("sourceTimestamp", sample.SourceTimestamp.UtcDateTime);
                writer.WriteString("serverTimestamp", sample.ServerTimestamp.UtcDateTime);
                writer.WriteNumber("statusCode", sample.StatusCode);
                writer.WriteString("quality", TransformationOutputValidator.Name(validated[index].Quality));
                WriteOptional(writer, "unit", tag.Unit);
                WriteOptional(writer, "minValid", tag.MinValid);
                WriteOptional(writer, "maxValid", tag.MaxValid);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] ContextJson(RegisteredTransformation transformation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", 1);
            writer.WriteString("label", transformation.Label);
            writer.WriteString("version", transformation.Version);
            writer.WritePropertyName("parameters");
            transformation.Parameters.WriteTo(writer);
            writer.WriteStartObject("outputs");
            foreach (var (name, output) in transformation.Outputs)
            {
                writer.WriteStartObject(name);
                writer.WriteString("unit", output.Unit);
                WriteOptional(writer, "minValid", output.MinValid);
                WriteOptional(writer, "maxValid", output.MaxValid);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }
}
