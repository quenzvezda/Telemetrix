using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Telemetrix.Integration.Tests;

public class RollupMigrationTests
{
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
            VALUES (now(),1,1,0,'00000000-0000-0000-0000-000000000001')
            """, cancellationToken: ct));
        await MigrateAsync(source, ct);
        await MigrateAsync(source, ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM measurement", cancellationToken: ct)));
        Assert.True(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('measurement_1m') IS NOT NULL", cancellationToken: ct)));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_refresh_continuous_aggregate'
            """, cancellationToken: ct)));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name='policy_retention'
            AND (config->>'drop_after')::interval IN (interval '30 days', interval '1 year')
            """, cancellationToken: ct)));
    }

    internal static async Task InitializeAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var command = source.CreateCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "init.sql"), ct));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task MigrateAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var command = source.CreateCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "sql", "migrations", "001_rollups_and_retention.sql"), ct));
        await command.ExecuteNonQueryAsync(ct);
    }
}
