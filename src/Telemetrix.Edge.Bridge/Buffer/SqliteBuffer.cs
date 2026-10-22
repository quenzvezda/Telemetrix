using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Telemetrix.Edge.Bridge.Buffer;

public sealed class SqliteBuffer : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly Lock gate = new();
    private readonly long maxBytes;
    private BufferDepth depth;
    private bool collectionPaused;

    public SqliteBuffer(IOptions<BufferOptions> options)
    {
        var settings = options.Value;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaxBytes);
        maxBytes = settings.MaxBytes;
        var path = System.IO.Path.GetFullPath(settings.Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS buffered_entries (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                topic TEXT NOT NULL,
                asset_key TEXT NOT NULL,
                payload BLOB NOT NULL,
                sample_count INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(sample_count), 0), COALESCE(SUM(length(payload)), 0) FROM buffered_entries";
        using var reader = command.ExecuteReader();
        reader.Read();
        depth = new BufferDepth(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        UpdateCollectionState();
    }

    public BufferDepth Depth { get { lock (gate) return depth; } }
    public bool IsCollectionPaused { get { lock (gate) return collectionPaused; } }

    // The caller retains ownership when the quota is full and retries after replay.
    public bool TryAppend(BufferedEntry entry)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entry.SampleCount);
        if (entry.Payload.LongLength > maxBytes)
            throw new InvalidOperationException("A serialized entry exceeds Buffer:MaxBytes; increase capacity.");
        lock (gate)
        {
            if (entry.Payload.LongLength > maxBytes - depth.Bytes) return false;
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO buffered_entries(topic, asset_key, payload, sample_count, created_at)
                VALUES($topic, $key, $payload, $samples, $created);
                """;
            command.Parameters.AddWithValue("$topic", entry.Topic);
            command.Parameters.AddWithValue("$key", entry.AssetKey);
            command.Parameters.AddWithValue("$payload", entry.Payload);
            command.Parameters.AddWithValue("$samples", entry.SampleCount);
            command.Parameters.AddWithValue("$created", entry.CreatedAt.ToUniversalTime().ToString("O"));
            command.ExecuteNonQuery();
            depth = new BufferDepth(depth.Batches + 1, depth.Samples + entry.SampleCount,
                depth.Bytes + entry.Payload.LongLength);
            UpdateCollectionState();
            return true;
        }
    }

    public BufferedEntry? ReadOldest()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT seq, topic, asset_key, payload, sample_count, created_at FROM buffered_entries ORDER BY seq LIMIT 1";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new BufferedEntry(reader.GetString(1), reader.GetString(2), (byte[])reader[3],
                reader.GetInt32(4), DateTimeOffset.Parse(reader.GetString(5),
                    System.Globalization.CultureInfo.InvariantCulture), reader.GetInt64(0));
        }
    }

    public void DeleteAcknowledged(BufferedEntry entry)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM buffered_entries WHERE seq=$seq";
            command.Parameters.AddWithValue("$seq", entry.Sequence);
            if (command.ExecuteNonQuery() == 0) return;
            depth = new BufferDepth(depth.Batches - 1, depth.Samples - entry.SampleCount,
                depth.Bytes - entry.Payload.LongLength);
            UpdateCollectionState();
        }
    }

    private void UpdateCollectionState()
    {
        if (depth.Bytes >= maxBytes * 0.8) collectionPaused = true;
        else if (depth.Bytes <= maxBytes * 0.5) collectionPaused = false;
    }

    public void Dispose() { lock (gate) connection.Dispose(); }
}
