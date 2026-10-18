using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;
using Testcontainers.PostgreSql;

namespace Telemetrix.Integration.Tests;

public class MeasurementWriterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BulkInsertPreservesMeasurementsAndReplayInsertsNothingTwice()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await InitializeAsync(source, ct);
        var writer = new MeasurementWriter(source, NullLogger<MeasurementWriter>.Instance);
        var batchId = Guid.NewGuid();
        var samples = Enumerable.Range(0, 35).Select(index => new ValidatedSample(index % 7 + 1,
            Timestamp.AddSeconds(index / 7).ToOffset(TimeSpan.FromHours(7)), index == 0 ? null : index,
            index == 0 ? Quality.Bad : (Quality)(index % 3))).ToArray();

        Assert.Equal(35, await writer.WriteAsync(batchId, samples, ct));
        Assert.Equal(0, await writer.WriteAsync(batchId, samples, ct));
        Assert.Equal(0, await writer.WriteAsync(Guid.NewGuid(), samples, ct));
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT tag_id AS "TagId", time AS "Time", value AS "Value", quality AS "Quality",
                   batch_id AS "BatchId", ingested_at AS "IngestedAt"
            FROM measurement ORDER BY time, tag_id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var stored = new List<StoredMeasurement>();
        while (await reader.ReadAsync(ct))
            stored.Add(new StoredMeasurement(reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2), reader.GetInt16(3), reader.GetGuid(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        Assert.Equal(35, stored.Count);
        for (var index = 0; index < samples.Length; index++)
        {
            Assert.Equal(samples[index].TagId, stored[index].TagId);
            Assert.Equal(samples[index].SourceTimestamp, stored[index].Time);
            Assert.Equal(TimeSpan.Zero, stored[index].Time.Offset);
            Assert.Equal(samples[index].Value, stored[index].Value);
            Assert.Equal((short)samples[index].Quality, stored[index].Quality);
            Assert.Equal(batchId, stored[index].BatchId);
            Assert.NotEqual(default, stored[index].IngestedAt);
        }
    }

    [Fact]
    public async Task ForcedMidInsertFailureRollsBackAndTheNextWriteCanSucceed()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        var settings = new NpgsqlConnectionStringBuilder(database.GetConnectionString()) { MaxPoolSize = 1 };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        await InitializeAsync(source, ct);
        await using (var connection = await source.OpenConnectionAsync(ct))
        {
            // Test-only trigger fails on the second staged sample, after an earlier insert was attempted.
            await connection.ExecuteAsync(new CommandDefinition("""
                CREATE FUNCTION reject_test_sample() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.value = -999 THEN RAISE EXCEPTION 'forced mid-write failure'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER reject_test_sample BEFORE INSERT ON measurement
                FOR EACH ROW EXECUTE FUNCTION reject_test_sample();
                """, cancellationToken: ct));
        }
        var writer = new MeasurementWriter(source, NullLogger<MeasurementWriter>.Instance);
        var batchId = Guid.NewGuid();
        ValidatedSample[] samples = [new(1, Timestamp, 1, Quality.Good),
            new(1, Timestamp.AddSeconds(1), -999, Quality.Bad), new(1, Timestamp.AddSeconds(2), 3, Quality.Good)];

        var failure = await Assert.ThrowsAsync<PostgresException>(() => writer.WriteAsync(batchId, samples, ct));
        Assert.Equal("P0001", failure.SqlState);
        await using (var connection = await source.OpenConnectionAsync(ct))
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM measurement", cancellationToken: ct)));
        }
        samples[1] = samples[1] with { Value = 2 };
        Assert.Equal(3, await writer.WriteAsync(batchId, samples, ct));
        Assert.Equal(0, await writer.WriteAsync(batchId, samples, ct));
    }

    [Fact]
    public async Task DuplicateIdentityWithinABatchKeepsTheFirstValue()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await InitializeAsync(source, ct);
        var writer = new MeasurementWriter(source, NullLogger<MeasurementWriter>.Instance);
        var batchId = Guid.NewGuid();

        Assert.Equal(1, await writer.WriteAsync(batchId,
            [new(1, Timestamp, 1, Quality.Good), new(1, Timestamp, 99, Quality.Bad)], ct));
        Assert.Equal(0, await writer.WriteAsync(batchId, [], ct));
        await using var connection = await source.OpenConnectionAsync(ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<double>(new CommandDefinition(
            "SELECT value FROM measurement", cancellationToken: ct)));
    }

    private static async Task InitializeAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "sql", "init.sql"), ct), cancellationToken: ct));
    }

    private sealed record StoredMeasurement(int TagId, DateTimeOffset Time, double? Value,
        short Quality, Guid BatchId, DateTimeOffset IngestedAt);
}
