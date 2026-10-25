using Dapper;
using Npgsql;
using Telemetrix.Contracts;

namespace Telemetrix.Transformer.Storage;

public sealed class PlantEventWriter(NpgsqlDataSource source)
{
    public async Task WriteAsync(PlantEvent plantEvent, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO plant_event(event_id,schema_version,event_type,plant_id,line_id,asset_id,
                                    endpoint,occurred_at,gap_start,gap_end,reason)
            VALUES (@EventId,@SchemaVersion,@EventType,@PlantId,@LineId,@AssetId,
                    @Endpoint,@OccurredAt,@GapStart,@GapEnd,@Reason)
            ON CONFLICT (event_id) DO NOTHING
            """, new
            {
                plantEvent.EventId, plantEvent.SchemaVersion, EventType = plantEvent.Type.ToString(),
                plantEvent.PlantId, plantEvent.LineId, plantEvent.AssetId, plantEvent.Endpoint,
                OccurredAt = plantEvent.OccurredAt.ToUniversalTime(),
                GapStart = plantEvent.GapStart?.ToUniversalTime(), GapEnd = plantEvent.GapEnd?.ToUniversalTime(), plantEvent.Reason
            }, cancellationToken: ct));
    }
}
