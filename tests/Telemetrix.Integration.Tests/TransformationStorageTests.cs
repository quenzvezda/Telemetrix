using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;
using Telemetrix.Transformer.Storage;
using Testcontainers.PostgreSql;

namespace Telemetrix.Integration.Tests;

public class TransformationStorageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly OutputTag Fahrenheit = new("degF", -4, 176);

    [Fact]
    public async Task ShellRunnerAppliesAllMigrationsTwiceOnAnExistingDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17")
            .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "sql", "migrate.sh"), ct), "/migrate.sh");
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "sql", "migrations"), "*.sql"))
            builder = builder.WithResourceMapping(await File.ReadAllBytesAsync(file, ct), "/migrations/" + Path.GetFileName(file));
        await using var database = builder.Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await RollupMigrationTests.InitializeAsync(source, ct);
        var runs = new TransformationRunStore(source);
        Assert.False(await runs.SchemaReadyAsync(ct));

        for (var run = 0; run < 2; run++)
        {
            var result = await database.ExecAsync(["/bin/sh", "-c", "PGUSER=postgres PGDATABASE=postgres /bin/sh /migrate.sh"], ct);
            Assert.True(result.ExitCode == 0, result.Stderr);
            Assert.Contains("Applying 003_transformations.sql", result.Stdout);
        }

        Assert.True(await runs.SchemaReadyAsync(ct));
        await using var connection = await source.OpenConnectionAsync(ct);
        Assert.Equal(7, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM tag WHERE derived_by IS NULL", cancellationToken: ct)));
    }

    [Fact]
    public async Task RunRecordRawAndDerivedRowsCommitTogetherAndTheFirstRecordIsFinal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        var tags = new PostgresDerivedTagCatalog(source);
        var derived = (await tags.ResolveAsync("Tank01", "celsius-to-fahrenheit", "TemperatureF", Fahrenheit, ct)).Tag!;
        var writer = new MeasurementWriter(source, NullLogger<MeasurementWriter>.Instance);
        var runs = new TransformationRunStore(source);
        var batchId = Guid.NewGuid();
        ValidatedSample[] raw = [new(7, T0, 25, Quality.Good), new(7, T0.AddSeconds(1), 30, Quality.Good)];

        Assert.False(await runs.ExistsAsync(batchId, "celsius-to-fahrenheit", ct));
        Assert.Equal(4, await writer.WriteAsync(batchId, raw, Record("sha256:" + new string('a', 64), derived.TagId, 77, 86), ct));
        Assert.True(await runs.ExistsAsync(batchId, "celsius-to-fahrenheit", ct));
        // A replay after a script change: raw rows and the run record stay, no derived row of the new version is added.
        Assert.Equal(0, await writer.WriteAsync(batchId, raw, Record("sha256:" + new string('b', 64), derived.TagId, 1, 2) with
        {
            Derived = [new ValidatedSample(derived.TagId, T0.AddSeconds(2), 99, Quality.Good)]
        }, ct));

        await using var connection = await source.OpenConnectionAsync(ct);
        Assert.Equal(new[] { (25d, 7), (30d, 7), (77d, derived.TagId), (86d, derived.TagId) }, (await connection.QueryAsync<(double, int)>(
            new CommandDefinition("SELECT value, tag_id FROM measurement ORDER BY tag_id, time", cancellationToken: ct))).ToArray());
        var run = await connection.QuerySingleAsync<(string Version, string Status, int Inputs, int Modified, int Flagged, int Outputs)>(
            new CommandDefinition("""
                SELECT script_version, status, input_count, modified_count, flagged_count, output_count
                FROM transformation_run WHERE batch_id = @batchId
                """, new { batchId }, cancellationToken: ct));
        Assert.Equal(("sha256:" + new string('a', 64), "succeeded", 2, 1, 1, 2), run);
        var disposition = await connection.QuerySingleAsync<(int Index, int TagId, DateTime Time, string Kind, string Reason)>(
            new CommandDefinition("SELECT sample_index, tag_id, time, disposition, reason FROM sample_disposition", cancellationToken: ct));
        Assert.Equal((1, 7, T0.AddSeconds(1).UtcDateTime, "flagged", "spike"), disposition);
    }

    [Fact]
    public async Task FailedTransactionStoresNeitherRawRowsNorTheRunRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        var writer = new MeasurementWriter(source, NullLogger<MeasurementWriter>.Instance);
        var record = Record("sha256:" + new string('a', 64), 7, 77, 86) with
        {
            Derived = [],
            Dispositions = [new SampleDisposition(1, 9999, T0, "flagged", "unknown tag id")]
        };

        await Assert.ThrowsAsync<PostgresException>(() => writer.WriteAsync(Guid.NewGuid(), [new(7, T0, 25, Quality.Good)], record, ct));

        await using var connection = await source.OpenConnectionAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT (SELECT count(*) FROM measurement) + (SELECT count(*) FROM transformation_run)", cancellationToken: ct)));
    }

    [Fact]
    public async Task DerivedTagsNeverOverlapRawTagsOrOtherLabels()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        var tags = new PostgresDerivedTagCatalog(source);

        var created = await tags.ResolveAsync("Tank01", "celsius-to-fahrenheit", "TemperatureF", Fahrenheit, ct);
        var raw = await new PostgresDerivedTagCatalog(source).ResolveAsync("Tank01", "celsius-to-fahrenheit", "Temperature", Fahrenheit, ct);
        var otherLabel = await new PostgresDerivedTagCatalog(source).ResolveAsync("Tank01", "tank-normalization", "TemperatureF", Fahrenheit, ct);
        var otherUnit = await new PostgresDerivedTagCatalog(source).ResolveAsync("Tank01", "celsius-to-fahrenheit", "TemperatureF", Fahrenheit with { Unit = "K" }, ct);
        var unknownAsset = await new PostgresDerivedTagCatalog(source).ResolveAsync("Mixer01", "celsius-to-fahrenheit", "TemperatureF", Fahrenheit, ct);

        Assert.Equal(("TemperatureF", "degF", -4d, 176d), (created.Tag!.Name, created.Tag.Unit, created.Tag.MinValid, created.Tag.MaxValid));
        Assert.Contains("raw Tag", raw.Conflict, StringComparison.Ordinal);
        Assert.Contains("belongs to transformation 'celsius-to-fahrenheit'", otherLabel.Conflict, StringComparison.Ordinal);
        Assert.Contains("not the declared 'K'", otherUnit.Conflict, StringComparison.Ordinal);
        Assert.Contains("not registered", unknownAsset.Conflict, StringComparison.Ordinal);
        // A raw Sample named like a derived Tag is unknown, so it cannot write into the derived series.
        var catalog = new PostgresTagCatalog(source);
        Assert.Null(await catalog.FindAsync("Tank01", "TemperatureF", ct));
        Assert.DoesNotContain(await catalog.LoadAsync(ct), tag => tag.Name == "TemperatureF");
        Assert.Null(await catalog.CreateAsync("Tank01", "TemperatureF", ct));
    }

    internal static async Task<PostgreSqlContainer> StartAsync(CancellationToken ct)
    {
        var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await RollupMigrationTests.InitializeAsync(source, ct);
        await ApplyTransformationMigrationAsync(source, ct);
        return database;
    }

    internal static async Task ApplyTransformationMigrationAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var command = source.CreateCommand(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "sql", "migrations", "003_transformations.sql"), ct));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static TransformationRecord Record(string version, int derivedTagId, double first, double second) => new(
        "celsius-to-fahrenheit", "Tank01", version, TransformationRecord.Succeeded, null, null, 2, 0, 1, 1, 0,
        [new ValidatedSample(derivedTagId, T0, first, Quality.Good), new ValidatedSample(derivedTagId, T0.AddSeconds(1), second, Quality.Uncertain)],
        [new SampleDisposition(1, 7, T0.AddSeconds(1), "flagged", "spike")], 3.5);
}
