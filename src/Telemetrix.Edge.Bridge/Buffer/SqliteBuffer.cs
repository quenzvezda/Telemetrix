using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Telemetrix.Edge.Bridge.Buffer;

public sealed class SqliteBuffer : IDisposable
{
    private readonly SqliteConnection connection;

    public SqliteBuffer(IOptions<BufferOptions> options)
    {
        var settings = options.Value;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaxBytes);
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
    }

    public void Dispose() => connection.Dispose();
}
