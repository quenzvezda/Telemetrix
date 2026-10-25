using Opc.Ua;
using Opc.Ua.Client;
using Telemetrix.Simulation;

namespace Telemetrix.Integration.Tests;

public sealed class PlantSimServerTests
{
    private static async Task<List<string>> BrowseNamesAsync(Browser browser, NodeId id, CancellationToken ct)
    {
        List<string> names = [];
        var references = await browser.BrowseAsync(id, ct);
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            names.Add(reference.BrowseName.Name!);
        }

        return names;
    }

    [Fact]
    public async Task AnonymousClientConnectsWithSecurityNone()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var simulator = new PlantSimFixture();
        await simulator.StartAsync(deadline.Token);
        using var session = await simulator.ConnectAsync(deadline.Token);
        var value = await session.ReadValueAsync(VariableIds.Server_ServerStatus_CurrentTime, deadline.Token);
        Assert.True(StatusCode.IsGood(value.StatusCode));
        Assert.Equal(BuiltInType.DateTime, value.WrappedValue.TypeInfo.BuiltInType);
    }

    [Fact]
    public async Task AddressSpaceHasSevenTypedTagsAndEngineeringUnits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var simulator = new PlantSimFixture();
        await simulator.StartAsync(ct);
        using var session = await simulator.ConnectAsync(ct);
        Assert.Equal("urn:telemetrix:plantsim", session.NamespaceUris.GetString(2));
        var browser = new Browser(session)
        {
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true
        };
        Assert.Contains("Plant01", await BrowseNamesAsync(browser, ObjectIds.ObjectsFolder, ct));
        Assert.Contains("Line01", await BrowseNamesAsync(browser, new NodeId("Plant01", 2), ct));
        var assets = await BrowseNamesAsync(browser, new NodeId("Plant01.Line01", 2), ct);
        Assert.Equal(new[] { "Pump01", "Simulation", "Tank01" }, assets.Order());
        foreach (var tag in PlantTags.All)
        {
            var children = await BrowseNamesAsync(browser, new NodeId($"Line01.{tag.Asset}", 2), ct);
            Assert.Contains(tag.Name, children);
            var value = await session.ReadValueAsync(new NodeId(tag.Identifier, 2), ct);
            Assert.True(StatusCode.IsGood(value.StatusCode));
            Assert.Equal(tag.IsBoolean ? BuiltInType.Boolean : BuiltInType.Double, value.WrappedValue.TypeInfo.BuiltInType);
            Assert.True(value.SourceTimestamp.ToDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));
            if (!tag.IsBoolean)
            {
                var units = await session.ReadValueAsync(new NodeId($"{tag.Identifier}.EngineeringUnits", 2), ct);
                Assert.True(StatusCode.IsGood(units.StatusCode));
                Assert.True(units.WrappedValue.TryGetStructure<EUInformation>(out EUInformation? information));
                Assert.NotNull(information);
                Assert.Equal(tag.Unit, information.DisplayName.Text);
                if (tag.Name == "FlowRate") Assert.Equal(0x4D5148, information.UnitId);
            }
        }
    }

    [Fact]
    public async Task AllTagsHaveIncreasingOneSecondSourceTimestamps()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var simulator = new PlantSimFixture();
        await simulator.StartAsync(ct);
        using var session = await simulator.ConnectAsync(ct);
        var previous = new Dictionary<string, DateTimeOffset>();
        for (var tick = 0; tick < 4; tick++)
        {
            foreach (var tag in PlantTags.All)
            {
                var value = await session.ReadValueAsync(new NodeId(tag.Identifier, 2), ct);
                var timestamp = value.SourceTimestamp.ToDateTimeOffset();
                Assert.Equal(TimeSpan.Zero, timestamp.Offset);
                if (previous.TryGetValue(tag.Key, out var before))
                    Assert.InRange((timestamp - before).TotalSeconds, 1, 2);
                previous[tag.Key] = timestamp;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(1100), ct);
        }
    }
}
