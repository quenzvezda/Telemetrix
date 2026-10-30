using System.Text;
using System.Text.Json;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Tests;

public class TelemetryJsonTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 10, 17, 9, 15, 1, TimeSpan.Zero);

    [Fact]
    public void DesignExampleRoundTripsThroughTheSharedContract()
    {
        var json = ReadDesignExample();
        var batch = TelemetryJson.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equal(1, batch.SchemaVersion);
        Assert.Equal(Guid.Parse("0192f6c1-6d2e-7c3a-9b11-5e2f0a7d4c88"), batch.BatchId);
        Assert.Equal("opcua", batch.Source);
        Assert.Equal("opc.tcp://plantsim:4840", batch.Endpoint);
        Assert.Equal("Plant01", batch.PlantId);
        Assert.Equal("Line01", batch.LineId);
        Assert.Equal("Pump01", batch.AssetId);
        Assert.Equal(Timestamp.AddSeconds(4).AddMilliseconds(12), batch.CreatedAt);
        Assert.Collection(batch.Samples,
            sample => Assert.Equal(new BatchSample("MotorTemperature", 71.3,
                Timestamp, Timestamp.AddMilliseconds(4), 0), sample),
            sample => Assert.Equal(new BatchSample("FlowRate", 52.8,
                Timestamp, Timestamp.AddMilliseconds(4), 0), sample));

        var bytes = TelemetryJson.Serialize(batch);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(new[] { "assetId", "batchId", "createdAt", "endpoint", "lineId",
                "plantId", "samples", "schemaVersion", "source" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "serverTimestamp", "sourceTimestamp", "statusCode", "tag", "value" },
            root.GetProperty("samples")[0].EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal));
        AssertUtcTimestamps(root);

        var roundTrip = TelemetryJson.Deserialize(bytes);
        Assert.Equal(batch.BatchId, roundTrip.BatchId);
        Assert.Equal(batch.SchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(batch.Source, roundTrip.Source);
        Assert.Equal(batch.Endpoint, roundTrip.Endpoint);
        Assert.Equal(batch.PlantId, roundTrip.PlantId);
        Assert.Equal(batch.LineId, roundTrip.LineId);
        Assert.Equal(batch.AssetId, roundTrip.AssetId);
        Assert.Equal(batch.CreatedAt, roundTrip.CreatedAt);
        Assert.Equal(batch.Samples, roundTrip.Samples);
    }

    [Fact]
    public void UnlabeledBatchesKeepTheVersionOneShape()
    {
        var batch = new TelemetryBatch(TelemetryBatch.SchemaVersionFor(null), Guid.NewGuid(), "opcua",
            "opc.tcp://plantsim:4840", "Plant01", "Line01", "Pump01", Timestamp,
            [new BatchSample("FlowRate", 52.8, Timestamp, Timestamp, 0)]);

        using var document = JsonDocument.Parse(TelemetryJson.Serialize(batch));

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("transformation", out _));
    }

    [Fact]
    public void LabeledBatchesRoundTripAsVersionTwo()
    {
        var batch = new TelemetryBatch(TelemetryBatch.SchemaVersionFor("celsius-to-fahrenheit"), Guid.NewGuid(),
            "opcua", "opc.tcp://plantsim:4840", "Plant01", "Line01", "Tank01", Timestamp,
            [new BatchSample("Temperature", 25, Timestamp, Timestamp, 0)], "celsius-to-fahrenheit");

        var bytes = TelemetryJson.Serialize(batch);
        using var document = JsonDocument.Parse(bytes);
        var validated = TelemetryJson.DeserializeValidated(bytes);

        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("celsius-to-fahrenheit", document.RootElement.GetProperty("transformation").GetString());
        Assert.Equal(2, validated.SchemaVersion);
        Assert.Equal("celsius-to-fahrenheit", validated.Transformation);
    }

    [Fact]
    public void NonUtcTimestampsPreserveTheirInstantsAndPrecision()
    {
        var source = Timestamp.ToOffset(TimeSpan.FromHours(7)).AddTicks(1234567);
        var server = source.AddMilliseconds(4).ToOffset(TimeSpan.FromHours(-3));
        var batch = CreateBatch([new BatchSample("FlowRate", 52.8, source, server, 0)], source);

        Assert.Equal(TimeSpan.Zero, batch.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, batch.Samples[0].SourceTimestamp.Offset);
        Assert.Equal(TimeSpan.Zero, batch.Samples[0].ServerTimestamp.Offset);
        using var document = JsonDocument.Parse(TelemetryJson.Serialize(batch));
        AssertUtcTimestamps(document.RootElement);

        var roundTrip = TelemetryJson.Deserialize(TelemetryJson.Serialize(batch));
        Assert.Equal(source, roundTrip.CreatedAt);
        Assert.Equal(source, roundTrip.Samples[0].SourceTimestamp);
        Assert.Equal(server, roundTrip.Samples[0].ServerTimestamp);
    }

    [Fact]
    public void IncomingOffsetsAreNormalizedToUtc()
    {
        var json = ReadDesignExample()
            .Replace("2026-10-17T09:15:05.012Z", "2026-10-17T16:15:05.012+07:00", StringComparison.Ordinal)
            .Replace("2026-10-17T09:15:01.000Z", "2026-10-17T16:15:01.000+07:00", StringComparison.Ordinal)
            .Replace("2026-10-17T09:15:01.004Z", "2026-10-17T06:15:01.004-03:00", StringComparison.Ordinal);

        var batch = TelemetryJson.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TimeSpan.Zero, batch.CreatedAt.Offset);
        Assert.Equal(Timestamp.AddSeconds(4).AddMilliseconds(12), batch.CreatedAt);
        Assert.All(batch.Samples, sample =>
        {
            Assert.Equal(TimeSpan.Zero, sample.SourceTimestamp.Offset);
            Assert.Equal(TimeSpan.Zero, sample.ServerTimestamp.Offset);
            Assert.Equal(Timestamp, sample.SourceTimestamp);
            Assert.Equal(Timestamp.AddMilliseconds(4), sample.ServerTimestamp);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AdaptedBooleansTravelAsJsonNumbers(int value)
    {
        var batch = CreateBatch([new BatchSample("Running", value, Timestamp, Timestamp, 0)]);

        var bytes = TelemetryJson.Serialize(batch);
        using var document = JsonDocument.Parse(bytes);
        var jsonValue = document.RootElement.GetProperty("samples")[0].GetProperty("value");

        Assert.Equal(JsonValueKind.Number, jsonValue.ValueKind);
        Assert.Equal(value, jsonValue.GetDouble());
        Assert.Equal(value, TelemetryJson.Deserialize(bytes).Samples[0].Value);
    }

    [Fact]
    public void MissingValueAndUnsignedStatusCodeSurviveRoundTrip()
    {
        var batch = CreateBatch([new BatchSample("Temperature", null,
            Timestamp, Timestamp, 0xFFFFFFFF)]);

        var bytes = TelemetryJson.Serialize(batch);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(JsonValueKind.Null,
            document.RootElement.GetProperty("samples")[0].GetProperty("value").ValueKind);
        var sample = TelemetryJson.Deserialize(bytes).Samples[0];
        Assert.Null(sample.Value);
        Assert.Equal(uint.MaxValue, sample.StatusCode);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("\"running\"")]
    public void ValuesOutsideTheNumericContractAreRejected(string value)
    {
        var json = ReadDesignExample().Replace("71.3", value, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => TelemetryJson.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void SharedOptionsUseTheSameWireFormat()
    {
        var batch = CreateBatch([new BatchSample("FlowRate", 52.8, Timestamp, Timestamp, 0)]);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, TelemetryJson.Options);
        Assert.Equal(TelemetryJson.Serialize(batch), bytes);
        var roundTrip = JsonSerializer.Deserialize<TelemetryBatch>(bytes, TelemetryJson.Options);
        Assert.NotNull(roundTrip);
        Assert.Equal(batch.Samples, roundTrip.Samples);
    }

    [Fact]
    public void BatchKeepsItsSamplesWhenTheCallerReusesTheBuffer()
    {
        var sample = new BatchSample("FlowRate", 52.8, Timestamp, Timestamp, 0);
        var buffer = new List<BatchSample> { sample };
        var batch = CreateBatch(buffer);

        buffer.Clear();

        Assert.Equal(sample, Assert.Single(batch.Samples));
    }

    private static TelemetryBatch CreateBatch(IReadOnlyList<BatchSample> samples,
        DateTimeOffset? createdAt = null) => new(TelemetryBatch.CurrentSchemaVersion,
        Guid.Parse("0192f6c1-6d2e-7c3a-9b11-5e2f0a7d4c88"), "opcua", "opc.tcp://plantsim:4840",
        "Plant01", "Line01", "Pump01", createdAt ?? Timestamp, samples);

    private static string ReadDesignExample()
    {
        var design = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "plant-telemetry-pipeline.md"));
        var section = design.IndexOf("### Batch message (`plant.telemetry.raw`)", StringComparison.Ordinal);
        Assert.True(section >= 0, "The design must include the raw batch example.");
        var fence = design.IndexOf("```json", section, StringComparison.Ordinal);
        Assert.True(fence >= 0, "The raw batch example must be JSON.");
        var start = fence + "```json".Length;
        var end = design.IndexOf("```", start, StringComparison.Ordinal);
        Assert.True(end > start, "The raw batch JSON fence must be closed.");
        return design[start..end].Trim();
    }

    private static void AssertUtcTimestamps(JsonElement batch)
    {
        Assert.EndsWith("Z", batch.GetProperty("createdAt").GetString());
        foreach (var sample in batch.GetProperty("samples").EnumerateArray())
        {
            Assert.EndsWith("Z", sample.GetProperty("sourceTimestamp").GetString());
            Assert.EndsWith("Z", sample.GetProperty("serverTimestamp").GetString());
        }
    }
}
