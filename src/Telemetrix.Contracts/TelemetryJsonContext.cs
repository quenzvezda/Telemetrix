using System.Text.Json.Serialization;

namespace Telemetrix.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TelemetryBatch))]
[JsonSerializable(typeof(BatchSample))]
[JsonSerializable(typeof(PlantEvent))]
[JsonSerializable(typeof(MqttTagPayload))]
internal partial class TelemetryJsonContext : JsonSerializerContext;
