using Microsoft.Data.Sqlite;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Buffer;
using System.Text.Json;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Tests;

public sealed class SqliteBufferTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        $"telemetrix-buffer-{Guid.NewGuid():N}");
    private string DatabasePath => System.IO.Path.Combine(directory, "bridge.db");

    [Fact]
    public void DefaultLocationMatchesTheDocumentedLocalApplicationDataDirectory()
    {
        Assert.Equal(System.IO.Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Telemetrix", "buffer", "bridge.db"),
            new BufferOptions().Path);
    }

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

    [Fact]
    public void ObservableMetricsFollowDurableDepthThroughReplay()
    {
        using var buffer = Open();
        var observed = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, buffer.Meter)) active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => observed[instrument.Name] = value);
        listener.Start();
        Assert.True(buffer.TryAppend(Entry(10, 5)));
        listener.RecordObservableInstruments();
        Assert.Equal(1, observed["bridge.buffer.batches"]);
        Assert.Equal(5, observed["bridge.buffer.samples"]);
        buffer.DeleteAcknowledged(buffer.ReadOldest()!);
        listener.RecordObservableInstruments();
        Assert.Equal(0, observed["bridge.buffer.batches"]);
        Assert.Equal(0, observed["bridge.buffer.samples"]);
    }

    [Fact]
    public void FifoBytesAndCountersSurviveRestartAndAcknowledgements()
    {
        var first = Entry(4, 3);
        var second = Entry(5, 0) with { Topic = "plant.events" };
        using (var buffer = Open())
        {
            Assert.True(buffer.TryAppend(first));
            Assert.True(buffer.TryAppend(second));
            Assert.Equal(new BufferDepth(2, 3, 9), buffer.Depth);
        }
        using var reopened = Open();
        Assert.Equal(new BufferDepth(2, 3, 9), reopened.Depth);
        var oldest = reopened.ReadOldest()!;
        Assert.Equal(first.Payload, oldest.Payload);
        reopened.DeleteAcknowledged(oldest);
        reopened.DeleteAcknowledged(oldest);
        Assert.Equal(new BufferDepth(1, 0, 5), reopened.Depth);
        var next = reopened.ReadOldest()!;
        Assert.True(next.Sequence > oldest.Sequence);
        Assert.Equal(second.Topic, next.Topic);
        Assert.Equal(second.Payload, next.Payload);
        reopened.DeleteAcknowledged(next);
        Assert.Null(reopened.ReadOldest());
        Assert.Equal(default, reopened.Depth);
    }

    [Fact]
    public void HardQuotaNeverEvictsAndPauseUsesHysteresis()
    {
        using var buffer = Open(100);
        Assert.True(buffer.TryAppend(Entry(40, 1)));
        Assert.False(buffer.IsCollectionPaused);
        Assert.True(buffer.TryAppend(Entry(40, 1)));
        Assert.True(buffer.IsCollectionPaused);
        Assert.False(buffer.TryAppend(Entry(21, 1)));
        Assert.Equal(80, buffer.Depth.Bytes);
        buffer.DeleteAcknowledged(buffer.ReadOldest()!);
        Assert.False(buffer.IsCollectionPaused);
        Assert.Equal(40, buffer.Depth.Bytes);
        Assert.Throws<InvalidOperationException>(() => buffer.TryAppend(Entry(101, 1)));
    }

    [Fact]
    public void ReopeningAbovePauseThresholdKeepsCollectionPaused()
    {
        using (var buffer = Open(100)) Assert.True(buffer.TryAppend(Entry(80, 1)));
        using var reopened = Open(100);
        Assert.True(reopened.IsCollectionPaused);
        Assert.Equal(80, reopened.Depth.Bytes);
    }

    [Fact]
    public void DefaultCapacityHoldsAnHourOfSevenTagsAtDefaultSamplingRate()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory, "config", "appsettings.Development.json")));
        var tags = document.RootElement.GetProperty("OpcUa").GetProperty("Tags")
            .EnumerateArray().Select(tag => (Asset: tag.GetProperty("Asset").GetString()!,
                Tag: tag.GetProperty("Tag").GetString()!)).ToArray();
        Assert.Equal(7, tags.Length);
        var start = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        long bytes = 0;
        // Worst-case notification every 500 ms, even if the simulator changes only every second.
        for (var window = 0; window < 720; window++)
        foreach (var asset in tags.GroupBy(tag => tag.Asset))
        {
            var samples = Enumerable.Range(0, 10).SelectMany(index => asset.Select(tag =>
                new BatchSample(tag.Tag, 123456.789012345, start.AddSeconds(window * 5 + index * 0.5),
                    start.AddSeconds(window * 5 + index * 0.5), uint.MaxValue))).ToArray();
            bytes += TelemetryJson.Serialize(new TelemetryBatch(1, Guid.NewGuid(), "opcua",
                "opc.tcp://localhost:50000", "Plant01", "Line01", asset.Key,
                start.AddSeconds(window * 5), samples)).Length;
        }
        Assert.True(bytes < new BufferOptions().MaxBytes * 0.5,
            $"One hour requires {bytes} payload bytes; keep headroom below the pause threshold.");
    }

    private static BufferedEntry Entry(int bytes, int samples) => new("plant.telemetry.raw", "Pump01",
        Enumerable.Range(0, bytes).Select(value => (byte)value).ToArray(), samples, DateTimeOffset.UtcNow);

    private SqliteBuffer Open(long maxBytes = 64 * 1024 * 1024) => new(Options.Create(
        new BufferOptions { Path = DatabasePath, MaxBytes = maxBytes }));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
