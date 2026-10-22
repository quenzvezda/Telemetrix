using Microsoft.Extensions.Time.Testing;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Edge.Bridge.Tests;

public class ReconnectBackoffTests
{
    [Fact]
    public void DelaySequenceDoublesAndStaysCapped()
    {
        Assert.Equal(new double[] { 1, 2, 4, 8, 15, 15, 15 },
            Enumerable.Range(0, 7).Select(attempt => ReconnectBackoff.Delay(attempt, 0.5).TotalSeconds));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var baseline = Math.Min(15, Math.Pow(2, attempt));
            Assert.InRange(ReconnectBackoff.Delay(attempt, 0).TotalSeconds, baseline * 0.8, 15);
            Assert.InRange(ReconnectBackoff.Delay(attempt, 1).TotalSeconds, baseline, Math.Min(15, baseline * 1.2));
        }
    }

    [Fact]
    public async Task DelayUsesInjectedClockAndCancellation()
    {
        var clock = new FakeTimeProvider();
        using var stopped = new CancellationTokenSource();
        var waiting = Task.Delay(ReconnectBackoff.Delay(2, 0.5), clock, stopped.Token);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await waiting;
        var cancelled = Task.Delay(ReconnectBackoff.Delay(4, 0.5), clock, stopped.Token);
        await stopped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }
}
