using Dapper;
using Npgsql;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Storage;

public sealed class TransformationRunStore(NpgsqlDataSource dataSource) : ITransformationRunStore
{
    public async Task<bool> ExistsAsync(Guid batchId, string label, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM transformation_run WHERE batch_id = @BatchId AND label = @Label)",
            new { BatchId = batchId, Label = label }, cancellationToken: cancellationToken));
    }

    public async Task<bool> SchemaReadyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT to_regclass('transformation_run') IS NOT NULL
               AND to_regclass('sample_disposition') IS NOT NULL
               AND EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'tag' AND column_name = 'derived_by')
            """, cancellationToken: cancellationToken));
    }
}
