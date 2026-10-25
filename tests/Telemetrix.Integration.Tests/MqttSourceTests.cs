using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources.Mqtt;

namespace Telemetrix.Integration.Tests;

public sealed class MqttSourceTests
{
    [Fact]
    public async Task PersistentSessionRecoversQueuedNumericAndBooleanSamplesWithOriginalTimes()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await using var mqtt = new MqttFixture();
        await mqtt.Broker.StartAsync(ct);
        using var publisher = await mqtt.ConnectAsync(ct);
        var options = Options.Create(new MqttOptions { Host = mqtt.Host, Port = mqtt.Port, ClientId = "persistent-test" });
        var events = new Events();
        var channel = Channel.CreateUnbounded<Sample>();
        var adapter = new MqttSourceAdapter(options, TimeProvider.System, NullLogger<MqttSourceAdapter>.Instance, events);
        var time = DateTimeOffset.UtcNow;
        await MqttFixture.PublishAsync(publisher, time, ct, retain: true);
        using (var run = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var running = adapter.RunAsync(channel.Writer, run.Token);
            var first = await channel.Reader.ReadAsync(ct);
            Assert.Equal(time, first.SourceTimestamp);
            Assert.Equal(52.8, first.Value);
            await run.CancelAsync();
            await running;
        }
        await MqttFixture.PublishAsync(publisher, time.AddSeconds(1), ct, "999");
        await MqttFixture.PublishAsync(publisher, time.AddSeconds(1), ct, "true", "Pump01/Running");
        options.Value.TopicFilter = "plant/Plant01/Line01/Pump01/Running";
        using var resumed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var restart = adapter.RunAsync(channel.Writer, resumed.Token);
        try
        {
            var recovered = await channel.Reader.ReadAsync(ct);
            Assert.Equal("Running", recovered.Tag);
            Assert.Equal("Plant01", recovered.Plant);
            Assert.Equal("Line01", recovered.Line);
            Assert.Equal("Pump01", recovered.Asset);
            Assert.Equal(1d, recovered.Value);
            Assert.Equal(time.AddSeconds(1), recovered.SourceTimestamp);
            Assert.Equal(TimeSpan.Zero, recovered.ServerTimestamp.Offset);
            Assert.Equal(0u, recovered.StatusCode);
        }
        finally { await resumed.CancelAsync(); await restart; }
    }

    [Fact]
    public async Task CapacityPausePreservesBufferClearsQueuedHistoryAndReportsOrderedGap()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await using var mqtt = new MqttFixture();
        await mqtt.Broker.StartAsync(ct);
        using var publisher = await mqtt.ConnectAsync(ct);
        var directory = Path.Combine(Path.GetTempPath(), $"telemetrix-mqtt-capacity-{Guid.NewGuid():N}");
        try
        {
            using var buffer = new SqliteBuffer(Options.Create(new BufferOptions
                { Path = Path.Combine(directory, "edge.db"), MaxBytes = 10000 }));
            var events = new Events();
            var options = Options.Create(new MqttOptions { Host = mqtt.Host, Port = mqtt.Port, ClientId = "capacity-test" });
            var channel = Channel.CreateUnbounded<Sample>();
            var adapter = new MqttSourceAdapter(options, TimeProvider.System, NullLogger<MqttSourceAdapter>.Instance, events, buffer);
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = adapter.RunAsync(channel.Writer, run.Token);
            var time = DateTimeOffset.UtcNow;
            try
            {
                await MqttFixture.PublishAsync(publisher, time, ct, retain: true);
                await channel.Reader.ReadAsync(ct);
                Assert.True(buffer.TryAppend(new BufferedEntry("test", "Pump01", new byte[8100], 1, time)));
                await WaitAsync(() => events.Values.Any(e => e.Type == PlantEventType.ConnectionLost), ct);
                Assert.Equal(1, buffer.Depth.Samples);
                await MqttFixture.PublishAsync(publisher, time.AddSeconds(1), ct, "999");
                buffer.DeleteAcknowledged(buffer.ReadOldest()!);
                await WaitAsync(() => events.Values.Any(e => e.Type == PlantEventType.ConnectionRestored), ct);
                await MqttFixture.PublishAsync(publisher, time.AddSeconds(2), ct, "53", retain: true);
                Sample fresh;
                do { fresh = await channel.Reader.ReadAsync(ct); Assert.NotEqual(999d, fresh.Value); }
                while (fresh.SourceTimestamp != time.AddSeconds(2));
                await WaitAsync(() => events.Values.Any(e => e.Type == PlantEventType.Gap), ct);
                var transitions = events.Values.ToArray();
                Assert.Equal(new[] { PlantEventType.ConnectionLost, PlantEventType.ConnectionRestored, PlantEventType.Gap },
                    transitions.Select(e => e.Type));
                Assert.All(transitions, e => { Assert.Equal("buffer-capacity", e.Reason); Assert.Equal(options.Value.Endpoint, e.Endpoint); });
                Assert.Equal(time, transitions[2].GapStart);
                Assert.Equal(time.AddSeconds(2), transitions[2].GapEnd);
            }
            finally { await run.CancelAsync(); await running; }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal static async Task WaitAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(25, ct);
    }

    private sealed class Events : IPlantEventPublisher
    {
        public ConcurrentQueue<PlantEvent> Values { get; } = new();
        public Task PublishAsync(PlantEvent plantEvent, CancellationToken cancellationToken)
        { Values.Enqueue(plantEvent); return Task.CompletedTask; }
    }
}
