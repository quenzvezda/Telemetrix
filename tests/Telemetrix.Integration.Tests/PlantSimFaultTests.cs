using Opc.Ua;
using Opc.Ua.Client;

namespace Telemetrix.Integration.Tests;

public sealed class PlantSimFaultTests
{
    [Fact]
    public async Task MethodsTripStartApplyBadQualityAndReplaceOneUpdateWithASpike()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        await using var simulator = new PlantSimFixture();
        await simulator.StartAsync(ct);
        using var session = await simulator.ConnectAsync(ct);
        await CallAsync(session, "TripPump", ct);
        await WaitForAsync(session, "Pump01.Running", value => !value.WrappedValue.GetBoolean(), ct);
        var flow = await session.ReadValueAsync(new NodeId("Line01.Pump01.FlowRate", 2), ct);
        Assert.Equal(0, flow.WrappedValue.GetDouble());
        await CallAsync(session, "StartPump", ct);
        await WaitForAsync(session, "Pump01.Running", value => value.WrappedValue.GetBoolean(), ct);
        await CallAsync(session, "SetBadQuality", ct, new Variant("Tank01.Temperature"), new Variant(2u));
        await WaitForAsync(session, "Tank01.Temperature", value => StatusCode.IsBad(value.StatusCode), ct);
        await WaitForAsync(session, "Tank01.Temperature", value => StatusCode.IsGood(value.StatusCode), ct);
        await CallAsync(session, "Spike", ct, new Variant("Pump01.Vibration"));
        var spike = await WaitForAsync(session, "Pump01.Vibration", value => value.WrappedValue.GetDouble() > 4.5, ct);
        var normal = await WaitForAsync(session, "Pump01.Vibration", value =>
            value.SourceTimestamp > spike.SourceTimestamp, ct);
        Assert.InRange(normal.WrappedValue.GetDouble(), 0.5, 4.5);

        var unknown = await Assert.ThrowsAsync<ServiceResultException>(() =>
            CallAsync(session, "Spike", ct, new Variant("missing")));
        Assert.Equal(StatusCodes.BadInvalidArgument, unknown.StatusCode);
        var boolean = await Assert.ThrowsAsync<ServiceResultException>(() =>
            CallAsync(session, "Spike", ct, new Variant("Pump01.Running")));
        Assert.Equal(StatusCodes.BadInvalidArgument, boolean.StatusCode);
        var delay = await Assert.ThrowsAsync<ServiceResultException>(() =>
            CallAsync(session, "RestartServer", ct, new Variant(6u)));
        Assert.Equal(StatusCodes.BadOutOfRange, delay.StatusCode);
    }

    [Fact]
    public async Task RestartDropsOldSessionAndAcceptsNewConnectionsWithinTenSeconds()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var simulator = new PlantSimFixture();
        await simulator.StartAsync(ct);
        using var session = await simulator.ConnectAsync(ct);
        var started = TimeProvider.System.GetTimestamp();
        await CallAsync(session, "RestartServer", ct, new Variant(2u));
        var dropped = false;
        while (!dropped)
        {
            try { await session.ReadValueAsync(new NodeId("Line01.Pump01.FlowRate", 2), ct); }
            catch (ServiceResultException) { dropped = true; }
            if (!dropped) await Task.Delay(100, ct);
        }
        using var recoveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        recoveryDeadline.CancelAfter(TimeSpan.FromSeconds(10) - TimeProvider.System.GetElapsedTime(started));
        while (true)
        {
            try
            {
                using var restored = await simulator.ConnectAsync(recoveryDeadline.Token);
                var value = await restored.ReadValueAsync(new NodeId("Line01.Pump01.FlowRate", 2), recoveryDeadline.Token);
                Assert.True(StatusCode.IsGood(value.StatusCode));
                Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
                break;
            }
            catch (ServiceResultException) { await Task.Delay(100, recoveryDeadline.Token); }
        }
    }

    internal static async Task CallAsync(ISession session, string method, CancellationToken ct, params Variant[] args) =>
        await session.CallAsync(new NodeId("Line01.Simulation", 2), new NodeId($"Line01.Simulation.{method}", 2), ct, args);

    internal static async Task<DataValue> WaitForAsync(ISession session, string tag, Func<DataValue, bool> predicate,
        CancellationToken ct)
    {
        while (true)
        {
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both,
                [new ReadValueId { NodeId = new NodeId($"Line01.{tag}", 2), AttributeId = Attributes.Value }], ct);
            var value = response.Results[0];
            if (predicate(value)) return value;
            await Task.Delay(50, ct);
        }
    }
}
