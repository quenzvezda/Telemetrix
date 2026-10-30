using Dapper;
using Npgsql;
using NpgsqlTypes;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;

namespace Telemetrix.Transformer.Storage;

public sealed class MeasurementWriter(NpgsqlDataSource dataSource, ILogger<MeasurementWriter> logger,
    TransformerMetrics? metrics = null, TimeProvider? timeProvider = null) : IMeasurementWriter
{
    public Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples,
        CancellationToken cancellationToken) => WriteAsync(batchId, samples, null, cancellationToken);

    public async Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples,
        TransformationRecord? transformation, CancellationToken cancellationToken)
    {
        if (samples.Count == 0 && transformation is null) return 0;
        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // The first durable run record is final: a replay or a newer script version adds no derived rows.
        var recorded = transformation is not null && await RecordRunAsync(connection, transaction, batchId,
            transformation, cancellationToken);
        var staged = recorded && transformation!.Derived.Count > 0 ? [.. samples, .. transformation.Derived] : samples;

        var inserted = 0;
        if (staged.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                CREATE TEMP TABLE measurement_stage
                (LIKE measurement INCLUDING DEFAULTS, sample_order integer NOT NULL) ON COMMIT DROP
                """, transaction: transaction, cancellationToken: cancellationToken));

            await using (var importer = await connection.BeginBinaryImportAsync("""
                COPY measurement_stage (time, tag_id, value, quality, batch_id, sample_order)
                FROM STDIN (FORMAT BINARY)
                """, cancellationToken))
            {
                for (var index = 0; index < staged.Count; index++)
                {
                    var sample = staged[index];
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

            inserted = await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO measurement (time, tag_id, value, quality, batch_id, ingested_at)
                SELECT time, tag_id, value, quality, batch_id, ingested_at
                FROM measurement_stage ORDER BY sample_order
                ON CONFLICT (tag_id, time) DO NOTHING
                """, transaction: transaction, cancellationToken: cancellationToken));
        }
        if (recorded && transformation!.Dispositions.Count > 0)
            await RecordDispositionsAsync(connection, transaction, batchId, transformation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics?.WriteCompleted(inserted, clock.GetElapsedTime(started));
        logger.LogInformation("Stored batch {BatchId}: staged={StagedCount}, inserted={InsertedCount}, duplicates={DuplicateCount}, derived={DerivedCount}",
            batchId, staged.Count, inserted, staged.Count - inserted, recorded ? transformation!.OutputCount : 0);
        return inserted;
    }

    private static async Task<bool> RecordRunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid batchId, TransformationRecord run, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO transformation_run (batch_id, label, asset_id, script_version, status, reason, error,
                input_count, unchanged_count, modified_count, flagged_count, rejected_count, output_count, duration_ms)
            VALUES (@BatchId, @Label, @AssetId, @ScriptVersion, @Status, @Reason, @Error,
                @InputCount, @UnchangedCount, @ModifiedCount, @FlaggedCount, @RejectedCount, @OutputCount, @DurationMs)
            ON CONFLICT (batch_id, label) DO NOTHING
            """, new
            {
                BatchId = batchId, run.Label, run.AssetId, run.ScriptVersion, run.Status, run.Reason, run.Error,
                run.InputCount, run.UnchangedCount, run.ModifiedCount, run.FlaggedCount, run.RejectedCount,
                run.OutputCount, run.DurationMs
            }, transaction: transaction, cancellationToken: cancellationToken)) == 1;

    private static async Task RecordDispositionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid batchId, TransformationRecord run, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO sample_disposition (batch_id, label, sample_index, tag_id, time, disposition, reason)
            SELECT @batch, @label, item.* FROM unnest(@indexes, @tags, @times, @kinds, @reasons) AS item
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        var items = run.Dispositions;
        command.Parameters.Add(new NpgsqlParameter<Guid>("batch", batchId));
        command.Parameters.Add(new NpgsqlParameter<string>("label", run.Label));
        command.Parameters.Add(new NpgsqlParameter<int[]>("indexes", items.Select(item => item.Index).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int[]>("tags", items.Select(item => item.TagId).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("times", items.Select(item => item.SourceTimestamp.UtcDateTime).ToArray())
            { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter<string[]>("kinds", items.Select(item => item.Disposition).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("reasons", items.Select(item => item.Reason).ToArray()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
