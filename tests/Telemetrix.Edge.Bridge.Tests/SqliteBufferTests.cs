using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Edge.Bridge.Tests;

public sealed class SqliteBufferTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        $"telemetrix-buffer-{Guid.NewGuid():N}");
    private string DatabasePath => System.IO.Path.Combine(directory, "bridge.db");

    [Fact]
    public void StartupCreatesDurableSchema()
    {
        using var buffer = Open();
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='buffered_entries'";
        Assert.Equal("buffered_entries", command.ExecuteScalar());
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
        command.CommandText = "PRAGMA synchronous";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    private SqliteBuffer Open(long maxBytes = 64 * 1024 * 1024) => new(Options.Create(
        new BufferOptions { Path = DatabasePath, MaxBytes = maxBytes }));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
