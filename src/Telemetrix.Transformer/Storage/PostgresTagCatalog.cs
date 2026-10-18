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

    public async Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<TagMetadata>(new CommandDefinition(
            $"SELECT {Columns} FROM tag", cancellationToken: cancellationToken))).AsList();
    }

    public async Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TagMetadata>(new CommandDefinition(
            $"SELECT {Columns} FROM tag WHERE asset_id = @AssetId AND name = @Tag",
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
            $"SELECT {Columns} FROM tag WHERE asset_id = @AssetId AND name = @Tag",
            parameters, cancellationToken: cancellationToken));
    }
}
