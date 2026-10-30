using System.Collections.Concurrent;
using Dapper;
using Npgsql;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Storage;

public sealed class PostgresDerivedTagCatalog(NpgsqlDataSource dataSource) : IDerivedTagCatalog
{
    private readonly ConcurrentDictionary<(string Asset, string Label, string Name), TagMetadata> cache = new();

    public async Task<DerivedTagResolution> ResolveAsync(string assetId, string label, string name, OutputTag declared,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue((assetId, label, name), out var cached)) return new DerivedTagResolution(cached, null);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var parameters = new
        {
            AssetId = assetId, Name = name, Label = label, declared.Unit, Min = declared.MinValid, Max = declared.MaxValid
        };
        // The registry seeds unit and range on creation; afterwards the tag row is authoritative.
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tag (asset_id, name, unit, min_valid, max_valid, derived_by)
            SELECT asset_id, @Name, @Unit, @Min, @Max, @Label FROM asset WHERE asset_id = @AssetId
            ON CONFLICT (asset_id, name) DO NOTHING
            """, parameters, cancellationToken: cancellationToken));
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("""
            SELECT tag_id AS "TagId", unit AS "Unit", min_valid AS "MinValid", max_valid AS "MaxValid",
                   derived_by AS "DerivedBy"
            FROM tag WHERE asset_id = @AssetId AND name = @Name
            """, parameters, cancellationToken: cancellationToken));
        if (row is null) return Conflict($"Asset '{assetId}' is not registered, so output Tag '{name}' cannot be created.");
        if (row.DerivedBy is null) return Conflict($"Output Tag '{name}' is a raw Tag of {assetId}; transformations never write raw Tags.");
        if (row.DerivedBy != label) return Conflict($"Output Tag '{name}' of {assetId} belongs to transformation '{row.DerivedBy}'.");
        if (row.Unit != declared.Unit)
            return Conflict($"Output Tag '{name}' of {assetId} is stored in '{row.Unit}', not the declared '{declared.Unit}'.");
        var tag = new TagMetadata(row.TagId, assetId, name, row.Unit, row.MinValid, row.MaxValid);
        cache[(assetId, label, name)] = tag;
        return new DerivedTagResolution(tag, null);
    }

    private static DerivedTagResolution Conflict(string message) => new(null, message);

    private sealed record Row(int TagId, string? Unit, double? MinValid, double? MaxValid, string? DerivedBy);
}
