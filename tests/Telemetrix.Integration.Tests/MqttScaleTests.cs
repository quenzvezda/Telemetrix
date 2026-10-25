using System.Collections.Concurrent;
using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using Telemetrix.Contracts;
using Telemetrix.MqttSim;

namespace Telemetrix.Integration.Tests;

public class MqttScaleTests
{
    [Fact]
    public async Task ClonesKeepDistinctIdentitiesAndCompleteAcknowledgedTicks()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var mqtt = new MqttFixture();
        await mqtt.Broker.StartAsync(ct);
        using var subscriber = await mqtt.ConnectAsync(ct);
        var samples = new ConcurrentDictionary<(string Topic, DateTimeOffset Time), bool>();
        var duplicates = 0;
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            var payload = TelemetryJson.DeserializeMqtt(args.ApplicationMessage.Payload.ToArray());
            if (!samples.TryAdd((args.ApplicationMessage.Topic, payload.SourceTimestamp), true)) Interlocked.Increment(ref duplicates);
            return Task.CompletedTask;
        };
        await subscriber.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("plant/#").Build(), ct);
        await using var app = MqttSimApplication.Build(["--Http:Port=0", $"--MqttSim:Host={mqtt.Host}", $"--MqttSim:Port={mqtt.Port}",
            "--MqttSim:LineCount=2", "--MqttSim:TickInterval=00:00:00.200", "--MqttSim:MaxInFlight=4"]);
        await app.StartAsync(ct);
        var simulator = app.Services.GetRequiredService<MqttSimulator>();
        while (samples.Keys.Select(sample => sample.Time).Distinct().Count() < 3) await Task.Delay(25, ct);
        await simulator.CompleteAsync(ct);
        while (samples.Count < simulator.PublishedSamples) await Task.Delay(25, ct);
        Assert.Equal(simulator.PublishedSamples, samples.Count);
        Assert.Equal(0, duplicates);
        Assert.Equal(14, samples.Keys.Select(sample => sample.Topic).Distinct().Count());
        Assert.Contains(samples.Keys, sample => sample.Topic == "plant/Plant01/Line01/Pump01/Running");
        Assert.Contains(samples.Keys, sample => sample.Topic == "plant/Plant01/Line01_0002/Pump01_0002/Running");
        Assert.All(samples.Keys.GroupBy(sample => sample.Time), tick => Assert.Equal(14, tick.Count()));
        Assert.False(simulator.IsReady);
        await app.StopAsync(ct);
    }
}
