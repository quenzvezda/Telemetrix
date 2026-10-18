using System.Text.Json;

namespace Telemetrix.Contracts;

public static class TelemetryJson
{
    private static readonly TelemetryJsonContext Context = new(CreateOptions());

    public static JsonSerializerOptions Options => Context.Options;

    public static byte[] Serialize(TelemetryBatch batch) =>
        JsonSerializer.SerializeToUtf8Bytes(batch, Context.TelemetryBatch);

    public static TelemetryBatch Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, Context.TelemetryBatch)
        ?? throw new JsonException("A telemetry batch cannot be null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new UtcDateTimeOffsetJsonConverter());
        return options;
    }
}
