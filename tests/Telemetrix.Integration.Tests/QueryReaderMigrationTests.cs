using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Telemetrix.Integration.Tests;

public class QueryReaderMigrationTests
{
    private static readonly string[] ReadableRelations =
        ["asset", "tag", "measurement", "measurement_1m", "plant_event", "transformation_run", "sample_disposition"];

    [Fact]
    public async Task ReaderRoleMayOnlySelectIncludingTablesAddedLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17")
            .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "sql", "migrate.sh"), ct), "/migrate.sh");
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "sql", "migrations"), "*.sql"))
            builder = builder.WithResourceMapping(await File.ReadAllBytesAsync(file, ct), "/migrations/" + Path.GetFileName(file));
        await using var database = builder.Build();
        await database.StartAsync(ct);
        await using var owner = NpgsqlDataSource.Create(database.GetConnectionString());
        await RollupMigrationTests.InitializeAsync(owner, ct);

        for (var run = 0; run < 2; run++)
        {
            var result = await database.ExecAsync(["/bin/sh", "-c", "PGUSER=postgres PGDATABASE=postgres /bin/sh /migrate.sh"], ct);
            Assert.True(result.ExitCode == 0, result.Stderr);
            Assert.Contains("Applying 004_query_reader.sql", result.Stdout);
        }

        // A later migration's table, created by the role that applies migrations.
        var password = Guid.NewGuid().ToString("N");
        await using (var connection = await owner.OpenConnectionAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition("CREATE TABLE later_table(id int)", cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                $"CREATE ROLE reader_check LOGIN PASSWORD '{password}' IN ROLE telemetrix_reader", cancellationToken: ct));
        }

        var readerConnection = new NpgsqlConnectionStringBuilder(database.GetConnectionString())
        { Username = "reader_check", Password = password }.ConnectionString;
        await using var reader = NpgsqlDataSource.Create(readerConnection);
        await using var session = await reader.OpenConnectionAsync(ct);
        foreach (var relation in ReadableRelations.Append("later_table"))
            await session.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT count(*) FROM {relation}", cancellationToken: ct));
        Assert.Equal(7, await session.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM tag", cancellationToken: ct)));

        string[] writes =
        [
            "INSERT INTO measurement(time,tag_id,value,quality,batch_id) VALUES (now(),1,1,0,gen_random_uuid())",
            "UPDATE tag SET unit = 'x' WHERE tag_id = 1",
            "DELETE FROM asset WHERE asset_id = 'Tank01'",
            "TRUNCATE plant_event",
            "CREATE TABLE reader_table(id int)",
        ];
        foreach (var write in writes)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                session.ExecuteAsync(new CommandDefinition(write, cancellationToken: ct)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }
}
