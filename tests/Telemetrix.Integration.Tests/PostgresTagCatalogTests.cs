using Dapper;
using Npgsql;
using Telemetrix.Transformer.Storage;
using Testcontainers.PostgreSql;

namespace Telemetrix.Integration.Tests;

public class PostgresTagCatalogTests
{
    [Fact]
    public async Task SeededMetadataAndConcurrentTagCreationUseTheRealSchema()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await database.StartAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "init.sql"), ct), cancellationToken: ct));
        var catalog = new PostgresTagCatalog(source);

        Assert.Equal(7, (await catalog.LoadAsync(ct)).Count);
        var flow = Assert.IsType<Telemetrix.Transformer.Pipeline.TagMetadata>(await catalog.FindAsync("Pump01", "FlowRate", ct));
        Assert.Equal("m3/h", flow.Unit);
        Assert.Equal(0d, flow.MinValid);
        Assert.Equal(80d, flow.MaxValid);
        Assert.Null(await catalog.FindAsync("Tank01", "FlowRate", ct));
        var created = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => catalog.CreateAsync("Pump01", "Torque", ct)));
        var first = Assert.IsType<Telemetrix.Transformer.Pipeline.TagMetadata>(created[0]);
        Assert.All(created, tag => Assert.Equal(first, tag));
        Assert.Null(first.Unit);
        Assert.Null(first.MinValid);
        Assert.Null(first.MaxValid);
        Assert.Equal(8, (await catalog.LoadAsync(ct)).Count);
        Assert.Null(await catalog.CreateAsync("UnknownAsset", "Torque", ct));
        const string quotedTag = "Torque'; DROP TABLE tag; --";
        Assert.Equal(quotedTag, (await catalog.CreateAsync("Pump01", quotedTag, ct))!.Name);
        Assert.Equal(9, (await catalog.LoadAsync(ct)).Count);
    }
}
