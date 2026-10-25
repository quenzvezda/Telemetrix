using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Batching;
using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Edge.Bridge.Tests;

public class BridgeMetricsTests
{
    [Fact]
    public async Task BatcherReportsAcceptedSamplesAndGaugeTracksDiskDepth()
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetrix-metrics-{Guid.NewGuid():N}.db");
        try
        {
            using var buffer = new SqliteBuffer(Options.Create(new BufferOptions { Path = path }));
            using var metrics = new BridgeMetrics(buffer);
            using var listener = new MeterListener();
            var values = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
            listener.InstrumentPublished = (instrument, target) =>
            {
                if (instrument.Meter == metrics.Meter) target.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                values.Add((instrument.Name, value, tags.ToArray())));
            listener.Start();
            var input = Channel.CreateUnbounded<Sample>();
            var output = Channel.CreateUnbounded<TelemetryBatch>();
            var now = DateTimeOffset.UtcNow;
            await input.Writer.WriteAsync(new Sample("Plant01", "Line01", "Pump01", "FlowRate", 50, now, now, 0), TestContext.Current.CancellationToken);
            input.Writer.Complete();
            var batcher = new AssetBatcher(new BatchingOptions { MaxSamples = 1 }, TimeProvider.System, "mqtt", "mqtt://test", metrics);
            await batcher.RunAsync(input.Reader, output.Writer, TestContext.Current.CancellationToken);
            Assert.Single(await output.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
            metrics.BatchProduced("Pump01");
            listener.RecordObservableInstruments();
            Assert.True(buffer.TryAppend(new BufferedEntry("test", "Pump01", [1], 1, now)));
            listener.RecordObservableInstruments();
            buffer.DeleteAcknowledged(buffer.ReadOldest()!);
            listener.RecordObservableInstruments();
            var received = Assert.Single(values, entry => entry.Name == "telemetrix.samples.received");
            Assert.Equal(1, received.Value);
            Assert.Contains(received.Tags, tag => tag.Key == "asset" && Equals(tag.Value, "Pump01"));
            Assert.Contains(received.Tags, tag => tag.Key == "source" && Equals(tag.Value, "mqtt"));
            Assert.Equal(1, Assert.Single(values, entry => entry.Name == "telemetrix.batches.produced").Value);
            Assert.Equal(new long[] { 0, 1, 0 }, values.Where(entry => entry.Name == "telemetrix.buffer.depth").Select(entry => entry.Value));
            Assert.All(values, entry => Assert.DoesNotContain(entry.Tags, tag => tag.Key is "tag" or "batchId"));
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }
}
