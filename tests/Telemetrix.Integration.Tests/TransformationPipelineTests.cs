using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dapper;
using Telemetrix.Contracts;

namespace Telemetrix.Integration.Tests;

// Issue #7 acceptance through Kafka and TimescaleDB with the real host and Python worker.
public class TransformationPipelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LabelsSelectScriptsAndEveryOutcomeIsRecordedWithRawMeasurementsIntact()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        await TransformationStorageTests.ApplyTransformationMigrationAsync(environment.Source, ct);
        using var scripts = new TransformationScripts()
            .Add("explode", "    return 1 / 0")
            .Add("slow", "    import time\n    time.sleep(30)")
            .Add("nan", "    return {'samples': [{'tag': 'LevelNorm', 'value': float('nan'), 'sourceTimestamp': batch['samples'][0]['sourceTimestamp'], 'inputs': [0]}]}",
                new JsonObject { ["LevelNorm"] = new JsonObject { ["unit"] = "1" } })
            .Add("empty", "    return {}")
            .Add("conflict", "    return {'samples': [{'tag': 'FlowRate', 'value': 1.0, 'sourceTimestamp': batch['samples'][0]['sourceTimestamp'], 'inputs': [0]}]}",
                new JsonObject { ["FlowRate"] = new JsonObject { ["unit"] = "m3/h" } });
        using var host = environment.Host(settings: scripts.Settings(timeoutMs: 1000));
        TelemetryBatch[] batches =
        [
            Batch(0, "Pump01", "pump-cleaning", ("FlowRate", 50), ("FlowRate", 52), ("FlowRate", 120), ("FlowRate", 79), ("DischargePressure", 3)),
            Batch(1, "Tank01", "celsius-to-fahrenheit", ("Temperature", 25), ("Temperature", 30), ("Level", 40)),
            Batch(2, "Pump01", null, ("FlowRate", 51)),
            Batch(3, "Tank01", "made-up", ("Level", 41)),
            Batch(4, "Tank01", "explode", ("Level", 42)),
            Batch(5, "Tank01", "slow", ("Level", 43)),
            Batch(6, "Tank01", "celsius-to-fahrenheit", ("Temperature", 26)),
            Batch(7, "Tank01", "nan", ("Level", 44)),
            Batch(8, "Tank01", "empty", ("Level", 45)),
            Batch(9, "Pump01", "conflict", ("FlowRate", 53))
        ];
        try
        {
            await host.StartAsync(ct);
            foreach (var batch in batches) await environment.ProduceAsync(batch, 0, ct);
            await environment.WaitForOffsetsAsync([batches.Length], ct);

            await using var connection = await environment.Source.OpenConnectionAsync(ct);
            // Raw Measurements are exactly the samples sent, unchanged by any script.
            var raw = (await connection.QueryAsync<(string Name, double Value)>(new CommandDefinition("""
                SELECT t.name, m.value FROM measurement m JOIN tag t USING (tag_id)
                WHERE t.derived_by IS NULL ORDER BY m.time, t.name
                """, cancellationToken: ct))).ToArray();
            Assert.Equal(batches.SelectMany(batch => batch.Samples).Select(sample => (sample.Tag, sample.Value!.Value))
                .OrderBy(sample => sample.Tag, StringComparer.Ordinal).Order(), raw.OrderBy(sample => sample.Name, StringComparer.Ordinal).Order());

            var runs = (await connection.QueryAsync<(Guid BatchId, string Label, string? Version, string Status, string? Reason, int Inputs, int Outputs)>(
                new CommandDefinition("SELECT batch_id, label, script_version, status, reason, input_count, output_count FROM transformation_run",
                    cancellationToken: ct))).ToDictionary(run => run.BatchId);
            Assert.Equal(9, runs.Count);
            Assert.DoesNotContain(batches[2].BatchId, runs.Keys);
            Assert.Equal(Version(scripts, "data_cleaning.py"), runs[batches[0].BatchId].Version);
            Assert.Equal(Version(scripts, "celsius_to_fahrenheit.py"), runs[batches[1].BatchId].Version);
            Assert.NotEqual(runs[batches[0].BatchId].Version, runs[batches[1].BatchId].Version);
            Assert.Equal(("succeeded", (string?)null, 5, 4), Outcome(batches[0]));
            Assert.Equal(("succeeded", (string?)null, 3, 2), Outcome(batches[1]));
            Assert.Equal(("failed", "unknown-label", 1, 0), Outcome(batches[3]));
            Assert.Null(runs[batches[3].BatchId].Version);
            Assert.Equal(("failed", "script-error", 1, 0), Outcome(batches[4]));
            Assert.Equal(("failed", "timeout", 1, 0), Outcome(batches[5]));
            Assert.Equal(("succeeded", (string?)null, 1, 1), Outcome(batches[6]));
            Assert.Equal(("failed", "malformed-output", 1, 0), Outcome(batches[7]));
            Assert.Equal(("succeeded", (string?)null, 1, 0), Outcome(batches[8]));
            Assert.Equal(("failed", "output-tag-conflict", 1, 0), Outcome(batches[9]));
            Assert.Contains("ZeroDivisionError", await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT error FROM transformation_run WHERE batch_id = @id", new { id = batches[4].BatchId }, cancellationToken: ct)));

            var derived = (await connection.QueryAsync<(string Name, string Unit, string Label, DateTime Time, double Value, short Quality)>(
                new CommandDefinition("""
                    SELECT t.name, t.unit, t.derived_by, m.time, m.value, m.quality FROM measurement m JOIN tag t USING (tag_id)
                    WHERE t.derived_by IS NOT NULL ORDER BY t.name, m.time
                    """, cancellationToken: ct))).ToArray();
            Assert.Equal([
                ("DischargePressureClean", "bar", "pump-cleaning", Time(0, 4), 3d, (short)0),
                ("FlowRateClean", "m3/h", "pump-cleaning", Time(0, 0), 50d, (short)0),
                ("FlowRateClean", "m3/h", "pump-cleaning", Time(0, 1), 52d, (short)0),
                ("FlowRateClean", "m3/h", "pump-cleaning", Time(0, 3), 52d, (short)1),
                ("TemperatureF", "degF", "celsius-to-fahrenheit", Time(1, 0), 77d, (short)0),
                ("TemperatureF", "degF", "celsius-to-fahrenheit", Time(1, 1), 86d, (short)0),
                ("TemperatureF", "degF", "celsius-to-fahrenheit", Time(6, 0), 78.8, (short)0)], derived);
            Assert.Equal([(2, "rejected"), (3, "flagged")], (await connection.QueryAsync<(int, string)>(new CommandDefinition(
                "SELECT sample_index, disposition FROM sample_disposition ORDER BY sample_index", cancellationToken: ct))).ToArray());
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM tag WHERE derived_by = 'conflict' OR (name = 'LevelNorm' AND asset_id = 'Tank01')", cancellationToken: ct)));

            (string, string?, int, int) Outcome(TelemetryBatch batch) =>
                (runs[batch.BatchId].Status, runs[batch.BatchId].Reason, runs[batch.BatchId].Inputs, runs[batch.BatchId].Outputs);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StopAsync(cleanup.Token);
        }
    }

    [Fact]
    public async Task DisabledScriptingStoresLabeledBatchesWithoutTheTransformationSchema()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        using var host = environment.Host();
        var labeled = Batch(0, "Tank01", "celsius-to-fahrenheit", ("Temperature", 25), ("Level", 40));
        var unlabeled = Batch(1, "Tank01", null, ("Temperature", 26));
        try
        {
            await host.StartAsync(ct);
            await environment.ProduceAsync(labeled, 0, ct);
            await environment.ProduceAsync(unlabeled, 0, ct);
            await environment.WaitForOffsetsAsync([2], ct);

            Assert.Equal(3, await environment.CountAsync(ct));
            await using var connection = await environment.Source.OpenConnectionAsync(ct);
            Assert.False(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT to_regclass('transformation_run') IS NOT NULL", cancellationToken: ct)));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StopAsync(cleanup.Token);
        }
    }

    [Fact]
    public async Task EnabledScriptingRefusesToStartWithoutTheMigration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        using var scripts = new TransformationScripts();
        using var host = environment.Host(settings: scripts.Settings());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(ct));

        Assert.Contains("003_transformations.sql", exception.Message, StringComparison.Ordinal);
    }

    internal static TelemetryBatch Batch(int sequence, string asset, string? label, params (string Tag, double Value)[] samples) =>
        new(TelemetryBatch.SchemaVersionFor(label), Guid.CreateVersion7(), "opcua", "opc.tcp://plantsim:4840", "Plant01",
            "Line01", asset, T0.AddMinutes(sequence),
            samples.Select((sample, index) => new BatchSample(sample.Tag, sample.Value, T0.AddMinutes(sequence).AddSeconds(index),
                T0.AddMinutes(sequence).AddSeconds(index), 0)).ToArray(), label);

    internal static DateTime Time(int sequence, int index) => T0.AddMinutes(sequence).AddSeconds(index).UtcDateTime;

    internal static string Version(TransformationScripts scripts, string file) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(scripts.Path, file))));
}
