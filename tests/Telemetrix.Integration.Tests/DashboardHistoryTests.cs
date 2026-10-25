using System.Globalization;
using System.Text.Json;
using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Telemetrix.Integration.Tests;

public class DashboardHistoryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingHistoryBreaksTrendsAndTimelineWithoutInventingPlantEvents(bool rollups, bool unchangedState)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await RollupMigrationTests.InitializeAsync(source, ct);
        await RollupMigrationTests.MigrateAsync(source, ct);
        await using (var migration = source.CreateCommand(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "sql", "migrations", "002_plant_events.sql"), ct)))
            await migration.ExecuteNonQueryAsync(ct);

        var end = DateTimeOffset.UtcNow;
        end = new DateTimeOffset(end.Ticks - end.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        var from = end - (rollups ? TimeSpan.FromDays(7) : TimeSpan.FromMinutes(15));
        var first = end - (rollups ? TimeSpan.FromDays(6) : TimeSpan.FromMinutes(10));
        var resumed = end - (rollups ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(5));
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO measurement(time,tag_id,value,quality,batch_id)
            SELECT time,tag_id,value,0,@Batch
            FROM (VALUES (@First,1,@InitialState),(@First+interval '1 second',1,@InitialState),
                         (@Resumed,1,1),(@Resumed+interval '1 second',1,1),
                         (@First,2,10),(@First+interval '1 second',2,10),
                         (@Resumed,2,20),(@Resumed+interval '1 second',2,20)) AS samples(time,tag_id,value)
            """, new { First = first, Resumed = resumed, InitialState = unchangedState ? 1 : 0, Batch = Guid.NewGuid() }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "CALL refresh_continuous_aggregate('measurement_1m',@From,@End)",
            new { From = from, End = end }, cancellationToken: ct));

        using var dashboard = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "grafana", "dashboards", "telemetrix.json"), ct));
        foreach (var panelId in new[] { 1, 2 })
        {
            var sql = dashboard.RootElement.GetProperty("panels").EnumerateArray()
                .Single(panel => panel.GetProperty("id").GetInt32() == panelId)
                .GetProperty("targets")[0].GetProperty("rawSql").GetString()!;
            sql = ExpandMacros(sql, from, end);
            var points = (await connection.QueryAsync<HistoryPoint>(new CommandDefinition(sql, cancellationToken: ct))).ToArray();
            Assert.Contains(points, point => point.Value is not null && point.Time < resumed.UtcDateTime);
            Assert.Contains(points, point => point.Value is not null && point.Time >= resumed.UtcDateTime);
            Assert.Contains(points, point => point.Value is null && point.Time > first.AddSeconds(1).UtcDateTime
                && point.Time < resumed.UtcDateTime);
            Assert.DoesNotContain(points, point => point.Value is not null
                && point.Time > first.AddSeconds(1).UtcDateTime && point.Time < resumed.UtcDateTime);
            // A display hole must not erase adjacent real samples or the resumed state.
            Assert.Contains(points, point => point.Time == resumed.UtcDateTime && point.Value is not null);
            if (panelId == 2)
                Assert.Null(points.OrderBy(point => point.Time).Last().Value);
        }
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM plant_event", cancellationToken: ct)));
    }

    private static string ExpandMacros(string sql, DateTimeOffset from, DateTimeOffset to)
    {
        var start = $"TIMESTAMPTZ '{from:O}'";
        var end = $"TIMESTAMPTZ '{to:O}'";
        return sql.Replace("${asset:sqlstring}", "'Pump01'", StringComparison.Ordinal)
            .Replace("${tag:sqlstring}", "'FlowRate'", StringComparison.Ordinal)
            .Replace("${__interval}", "1 second", StringComparison.Ordinal)
            .Replace("$__timeFilter(m.time)", $"m.time BETWEEN {start} AND {end}", StringComparison.Ordinal)
            .Replace("$__timeFilter(r.bucket)", $"r.bucket BETWEEN {start} AND {end}", StringComparison.Ordinal)
            .Replace("$__timeFrom()", start, StringComparison.Ordinal)
            .Replace("$__timeTo()", end, StringComparison.Ordinal)
            .Replace("$__unixEpochFrom()", from.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$__unixEpochTo()", to.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private sealed record HistoryPoint(DateTime Time, double? Value, string Metric);
}
