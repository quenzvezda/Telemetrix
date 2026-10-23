using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using Telemetrix.Edge.Bridge.Sources;
using System.Threading.Channels;

namespace Telemetrix.Edge.Bridge.Tests;

public class PlantEventTrackerTests
{
    [Theory]
    [InlineData("source-unavailable")]
    [InlineData("buffer-capacity")]
    public async Task InterruptionEmitsOneOrderedTransitionAndSourceTimestampGapPerAsset(string reason)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));
        var publisher = new CapturingPublisher();
        using var tracker = new PlantEventTracker(Options.Create(new OpcUaOptions
        {
            Plant = "Plant01", Line = "Line01", Endpoint = "opc.tcp://test:50000"
        }), publisher, clock);
        tracker.TrackAssets(["Pump01", "Tank01"]);
        var ct = TestContext.Current.CancellationToken;
        var before = clock.GetUtcNow().AddSeconds(-2);
        var source = new InterruptedSource(tracker, clock, reason);
        var samples = Channel.CreateUnbounded<Sample>();
        await source.RunAsync(samples.Writer, ct);
        Assert.Equal(4, samples.Reader.Count); // Only the real before/after samples, no gap fill.
        Assert.Equal(6, publisher.Events.Count);
        Assert.Equal(6, publisher.Events.Select(value => value.EventId).Distinct().Count());
        foreach (var asset in new[] { "Pump01", "Tank01" })
        {
            var events = publisher.Events.Where(value => value.AssetId == asset).ToArray();
            Assert.Equal(new[] { PlantEventType.ConnectionLost, PlantEventType.ConnectionRestored, PlantEventType.Gap },
                events.Select(value => value.Type));
            Assert.All(events, value => Assert.Equal(reason, value.Reason));
            Assert.Equal(before, events[2].GapStart);
            Assert.Equal(clock.GetUtcNow(), events[2].GapEnd);
            Assert.All(events, value => Assert.Equal(TimeSpan.Zero, value.OccurredAt.Offset));
        }
    }

    private static Sample Sample(string asset, DateTimeOffset time) =>
        new("Plant01", "Line01", asset, "FlowRate", 1, time, time, 0);

    private sealed class InterruptedSource(PlantEventTracker tracker, FakeTimeProvider clock,
        string reason) : ISourceAdapter
    {
        public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken ct)
        {
            await tracker.RestoredAsync(ct);
            var before = clock.GetUtcNow().AddSeconds(-2);
            foreach (var asset in new[] { "Pump01", "Tank01" })
            {
                var sample = Sample(asset, before);
                await tracker.SampleAsync(sample, ct);
                await writer.WriteAsync(sample, ct);
            }
            await tracker.LostAsync(reason, ct);
            await tracker.LostAsync(reason, ct);
            clock.Advance(TimeSpan.FromSeconds(30));
            await tracker.RestoredAsync(ct);
            await tracker.RestoredAsync(ct);
            foreach (var asset in new[] { "Tank01", "Pump01" })
            {
                await tracker.SampleAsync(Sample(asset, before), ct);
                var sample = Sample(asset, clock.GetUtcNow());
                await tracker.SampleAsync(sample, ct);
                await writer.WriteAsync(sample, ct);
                await tracker.SampleAsync(Sample(asset, clock.GetUtcNow().AddSeconds(1)), ct);
            }
        }
    }

    private sealed class CapturingPublisher : IPlantEventPublisher
    {
        public List<PlantEvent> Events { get; } = [];
        public Task PublishAsync(PlantEvent plantEvent, CancellationToken cancellationToken)
        {
            Events.Add(plantEvent);
            return Task.CompletedTask;
        }
    }
}
