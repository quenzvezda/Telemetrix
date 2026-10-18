using Dapper;
using Npgsql;
using NpgsqlTypes;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Storage;

public sealed class MeasurementWriter(NpgsqlDataSource dataSource, ILogger<MeasurementWriter> logger) : IMeasurementWriter
{
    public async Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples,
        CancellationToken cancellationToken)
    {
        if (samples.Count == 0) return 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TEMP TABLE measurement_stage
            (LIKE measurement INCLUDING DEFAULTS, sample_order integer NOT NULL) ON COMMIT DROP
            """, transaction: transaction, cancellationToken: cancellationToken));

        await using (var importer = await connection.BeginBinaryImportAsync("""
            COPY measurement_stage (time, tag_id, value, quality, batch_id, sample_order)
            FROM STDIN (FORMAT BINARY)
            """, cancellationToken))
        {
            for (var index = 0; index < samples.Count; index++)
            {
                var sample = samples[index];
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(sample.SourceTimestamp.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await importer.WriteAsync(sample.TagId, NpgsqlDbType.Integer, cancellationToken);
                if (sample.Value is { } value)
                    await importer.WriteAsync(value, NpgsqlDbType.Double, cancellationToken);
                else
                    await importer.WriteNullAsync(cancellationToken);
                await importer.WriteAsync((short)sample.Quality, NpgsqlDbType.Smallint, cancellationToken);
                await importer.WriteAsync(batchId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(index, NpgsqlDbType.Integer, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }

        var inserted = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO measurement (time, tag_id, value, quality, batch_id, ingested_at)
            SELECT time, tag_id, value, quality, batch_id, ingested_at
            FROM measurement_stage ORDER BY sample_order
            ON CONFLICT (tag_id, time) DO NOTHING
            """, transaction: transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Stored batch {BatchId}: staged={StagedCount}, inserted={InsertedCount}, duplicates={DuplicateCount}",
            batchId, samples.Count, inserted, samples.Count - inserted);
        return inserted;
    }
}
