using System.Text.Json;

namespace Telemetrix.Contracts;

public static class TelemetryJson
{
    private static readonly TelemetryJsonContext Context = new(CreateOptions());

    public static JsonSerializerOptions Options => Context.Options;

    public static byte[] Serialize(MqttTagPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, Context.MqttTagPayload);

    public static MqttTagPayload DeserializeMqtt(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, Context.MqttTagPayload)
        ?? throw new JsonException("An MQTT payload cannot be null.");

    public static byte[] Serialize(TelemetryBatch batch) =>
        JsonSerializer.SerializeToUtf8Bytes(batch, Context.TelemetryBatch);

    public static byte[] Serialize(PlantEvent plantEvent) =>
        JsonSerializer.SerializeToUtf8Bytes(plantEvent, Context.PlantEvent);

    public static PlantEvent DeserializePlantEvent(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, Context.PlantEvent)
        ?? throw new JsonException("A plant event cannot be null.");

    public static TelemetryBatch Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, Context.TelemetryBatch)
        ?? throw new JsonException("A telemetry batch cannot be null.");

    public static TelemetryBatch DeserializeValidated(byte[]? utf8Json)
    {
        if (utf8Json is null)
            throw new TelemetryValidationException("schema-violation", "A telemetry batch cannot be a Kafka tombstone.");
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8Json); }
        catch (JsonException exception)
        {
            throw new TelemetryValidationException("invalid-json", exception.Message, exception);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
                throw new TelemetryValidationException("schema-violation", "schemaVersion must be an integer in a batch object.");
            if (number != TelemetryBatch.CurrentSchemaVersion)
                throw new TelemetryValidationException("unsupported-schema-version", $"Unsupported schemaVersion: {number}.");
        }

        TelemetryBatch batch;
        try { batch = Deserialize(utf8Json); }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new TelemetryValidationException("schema-violation", exception.Message, exception);
        }
        if (batch.BatchId == Guid.Empty || string.IsNullOrWhiteSpace(batch.Source)
            || string.IsNullOrWhiteSpace(batch.Endpoint) || string.IsNullOrWhiteSpace(batch.PlantId)
            || string.IsNullOrWhiteSpace(batch.LineId) || string.IsNullOrWhiteSpace(batch.AssetId)
            || batch.Samples.Count == 0 || batch.Samples.Any(sample => sample is null || string.IsNullOrWhiteSpace(sample.Tag)))
            throw new TelemetryValidationException("schema-violation", "Batch identity and sample tags must be present and nonempty.");
        return batch;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new UtcDateTimeOffsetJsonConverter());
        return options;
    }
}
