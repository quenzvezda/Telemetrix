using System.Text.Json.Serialization;

namespace Telemetrix.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TelemetryBatch))]
[JsonSerializable(typeof(BatchSample))]
internal partial class TelemetryJsonContext : JsonSerializerContext;
