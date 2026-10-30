using Dapper;
using Npgsql;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Storage;

public sealed class PostgresTagCatalog(NpgsqlDataSource dataSource) : ITagCatalog
{
    private const string Columns = """
        tag_id AS "TagId", asset_id AS "AssetId", name AS "Name",
        unit AS "Unit", min_valid AS "MinValid", max_valid AS "MaxValid"
        """;
    private string? rawOnly;

    public async Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<TagMetadata>(new CommandDefinition(
            $"SELECT {Columns} FROM tag WHERE {await RawOnlyAsync(connection, cancellationToken)}",
            cancellationToken: cancellationToken))).AsList();
    }

    public async Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TagMetadata>(new CommandDefinition(
            $"SELECT {Columns} FROM tag WHERE asset_id = @AssetId AND name = @Tag AND {await RawOnlyAsync(connection, cancellationToken)}",
            new { AssetId = assetId, Tag = tag }, cancellationToken: cancellationToken));
    }

    public async Task<TagMetadata?> CreateAsync(string assetId, string tag, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var parameters = new { AssetId = assetId, Tag = tag };
        var created = await connection.QuerySingleOrDefaultAsync<TagMetadata>(new CommandDefinition(
            $"""
            INSERT INTO tag (asset_id, name)
            SELECT asset_id, @Tag FROM asset WHERE asset_id = @AssetId
            ON CONFLICT (asset_id, name) DO NOTHING
            RETURNING {Columns}
            """, parameters, cancellationToken: cancellationToken));
        // A concurrent creator can win the insert. Re-select after its commit is visible.
        return created ?? await connection.QuerySingleOrDefaultAsync<TagMetadata>(new CommandDefinition(
            $"SELECT {Columns} FROM tag WHERE asset_id = @AssetId AND name = @Tag AND {await RawOnlyAsync(connection, cancellationToken)}",
            parameters, cancellationToken: cancellationToken));
    }

    // Raw Samples never resolve to a derived Tag (ADR-0018); the column exists once migration 003 is applied.
    private async Task<string> RawOnlyAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
        rawOnly ??= await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'tag' AND column_name = 'derived_by')
            """, cancellationToken: cancellationToken)) ? "derived_by IS NULL" : "TRUE";
}
