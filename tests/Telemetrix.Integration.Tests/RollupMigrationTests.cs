using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Telemetrix.Integration.Tests;

public class RollupMigrationTests
{
    [Fact]
    public async Task ShellRunnerAppliesBothMigrationsTwiceWithoutChangingMeasurements()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17")
            .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "sql", "migrations", "001_rollups_and_retention.sql"), ct), "/migrations/001_rollups_and_retention.sql")
            .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "sql", "migrations", "002_plant_events.sql"), ct), "/migrations/002_plant_events.sql")
            .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "sql", "migrate.sh"), ct), "/migrate.sh").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await InitializeAsync(source, ct);
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO measurement(time,tag_id,value,quality,batch_id)
            VALUES (now()-interval '2 days',1,1,0,'00000000-0000-0000-0000-000000000001')
            """, cancellationToken: ct));
        for (var run = 0; run < 2; run++)
        {
            var result = await database.ExecAsync(["/bin/sh", "-c", "PGUSER=postgres PGDATABASE=postgres /bin/sh /migrate.sh"], ct);
            Assert.True(result.ExitCode == 0, result.Stderr);
            Assert.Contains("Applying 001_rollups_and_retention.sql", result.Stdout);
            Assert.Contains("Applying 002_plant_events.sql", result.Stdout);
        }
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM measurement", cancellationToken: ct)));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT sample_count FROM measurement_1m", cancellationToken: ct)));
        Assert.True(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('plant_event') IS NOT NULL", cancellationToken: ct)));
        var fixture = await database.ExecAsync(["/bin/sh", "-c",
            "echo 'SELECT missing_m8_function();' > /migrations/003_error.sql; echo 'CREATE TABLE m8_should_not_exist(id int);' > /migrations/004_after_error.sql"], ct);
        Assert.Equal(0, fixture.ExitCode);
        var failed = await database.ExecAsync(["/bin/sh", "-c", "PGUSER=postgres PGDATABASE=postgres /bin/sh /migrate.sh"], ct);
        Assert.NotEqual(0, failed.ExitCode);
        Assert.True(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('m8_should_not_exist') IS NULL", cancellationToken: ct)));
    }

    [Fact]
    public async Task RetentionJobsTargetRawMeasurementsAndRollups()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await InitializeAsync(source, ct);
        await MigrateAsync(source, ct);
        await using var command = source.CreateCommand("""
            SELECT coalesce(c.view_name,j.hypertable_name),j.config->>'drop_after'
            FROM timescaledb_information.jobs j
            LEFT JOIN timescaledb_information.continuous_aggregates c
              ON c.materialization_hypertable_schema=j.hypertable_schema
             AND c.materialization_hypertable_name=j.hypertable_name
            WHERE j.proc_name='policy_retention'
            ORDER BY 1
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var policies = new List<(string Relation, string Retention)>();
        while (await reader.ReadAsync(ct)) policies.Add((reader.GetString(0), reader.GetString(1)));
        Assert.Equal(2, policies.Count);
        Assert.Contains(("measurement", "30 days"), policies);
        Assert.Contains(("measurement_1m", "1 year"), policies);
        foreach (var policy in policies) Console.WriteLine("Retention policy: {0} = {1}", policy.Relation, policy.Retention);
    }

    [Fact]
    public async Task RollupIncludesOnlyGoodMeasurements()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await InitializeAsync(source, ct);
        await MigrateAsync(source, ct);
        await using var connection = await source.OpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var start = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO measurement(time,tag_id,value,quality,batch_id)
            VALUES (@Start,2,10,0,@Batch), (@Start + interval '10 seconds',2,20,0,@Batch),
                   (@Start + interval '20 seconds',2,30,0,@Batch), (@Start + interval '30 seconds',2,999,2,@Batch)
            """, new { Start = start, Batch = Guid.NewGuid() }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("CALL refresh_continuous_aggregate('measurement_1m', @Start, @End)",
            new { Start = start, End = start.AddMinutes(1) }, cancellationToken: ct));
        await using var command = new NpgsqlCommand("SELECT min_value,max_value,avg_value,sample_count FROM measurement_1m", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(10, reader.GetDouble(0));
        Assert.Equal(30, reader.GetDouble(1));
        Assert.Equal(20, reader.GetDouble(2));
        Assert.Equal(3, reader.GetInt64(3));
        Assert.False(await reader.ReadAsync(ct));
    }

    [Fact]
    public async Task MigrationIsIdempotentAndPreservesExistingMeasurements()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await InitializeAsync(source, ct);
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO measurement(time,tag_id,value,quality,batch_id)
            VALUES (now()-interval '2 days',1,1,0,'00000000-0000-0000-0000-000000000001')
            """, cancellationToken: ct));
        await MigrateAsync(source, ct);
        await MigrateAsync(source, ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM measurement", cancellationToken: ct)));
        Assert.True(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('measurement_1m') IS NOT NULL", cancellationToken: ct)));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT sample_count FROM measurement_1m", cancellationToken: ct)));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_refresh_continuous_aggregate'
            """, cancellationToken: ct)));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name='policy_retention'
            AND (config->>'drop_after')::interval IN (interval '30 days', interval '1 year')
            """, cancellationToken: ct)));
        // Simulate retained rollups whose underlying raw history has expired.
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM measurement;
            INSERT INTO measurement(time,tag_id,value,quality,batch_id)
            VALUES (now()-interval '5 minutes',1,1,0,'00000000-0000-0000-0000-000000000002')
            """, cancellationToken: ct));
        await MigrateAsync(source, ct);
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM measurement_1m", cancellationToken: ct)));
    }

    internal static async Task InitializeAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var command = source.CreateCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "init.sql"), ct));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task MigrateAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "sql", "migrations", "001_rollups_and_retention.sql"), ct);
        var backfill = script.IndexOf("-- Populate retained history", StringComparison.Ordinal);
        // Match psql -f's separate command execution: the refresh procedure must
        // not run in Npgsql's multi-command pipeline with the preceding DDL.
        foreach (var statement in new[] { script[..backfill], script[backfill..] })
        {
            await using var command = source.CreateCommand(statement);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
