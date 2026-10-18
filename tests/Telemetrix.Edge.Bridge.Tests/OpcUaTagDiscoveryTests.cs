using Opc.Ua;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using BrowseNode = Telemetrix.Edge.Bridge.Sources.OpcUa.OpcUaTagDiscovery.BrowseNode;

namespace Telemetrix.Edge.Bridge.Tests;

public class OpcUaTagDiscoveryTests
{
    [Fact]
    public async Task DiscoversVariablesUnderTheirParentAssetsAndStopsCyclesAndDuplicateReferences()
    {
        var line = Node("Line01", NodeClass.Object);
        var pump = Node("Pump01", NodeClass.Object);
        var tank = Node("Tank01", NodeClass.Object);
        var running = Node("Running", NodeClass.Variable);
        var temperature = Node("Temperature", NodeClass.Variable);
        var graph = new Dictionary<NodeId, IReadOnlyList<BrowseNode>>
        {
            [line.NodeId] = [pump, tank],
            [pump.NodeId] = [running, line],
            [tank.NodeId] = [temperature, running],
            [running.NodeId] = [],
            [temperature.NodeId] = []
        };

        var tags = await OpcUaTagDiscovery.DiscoverAsync(line,
            (id, _) => Task.FromResult(graph[id]), TestContext.Current.CancellationToken);

        Assert.Equal(2, tags.Count);
        var pumpTag = Assert.Single(tags, tag => tag.Tag == "Running");
        Assert.Equal("Pump01", pumpTag.Asset);
        Assert.Equal(running.NodeId.ToString(), pumpTag.NodeId);
        Assert.Equal("Tank01", Assert.Single(tags, tag => tag.Tag == "Temperature").Asset);
    }

    [Fact]
    public async Task ResolvesAnObjectsBrowsePath()
    {
        var plant = Node("Plant01", NodeClass.Object);
        var line = Node("Line01", NodeClass.Object);
        var graph = new Dictionary<NodeId, IReadOnlyList<BrowseNode>>
        {
            [ObjectIds.ObjectsFolder] = [plant],
            [plant.NodeId] = [line]
        };

        var nodeId = await OpcUaTagDiscovery.ResolveStartAsync("Objects/Plant01/Line01",
            (id, _) => Task.FromResult(graph[id]), TestContext.Current.CancellationToken);

        Assert.Equal(line.NodeId, nodeId);
    }

    [Fact]
    public async Task AcceptsAnExplicitNodeIdWithoutBrowsingForTheStart()
    {
        var nodeId = await OpcUaTagDiscovery.ResolveStartAsync("ns=3;s=Telemetry",
            (_, _) => throw new InvalidOperationException("No browse expected"),
            TestContext.Current.CancellationToken);

        Assert.Equal(NodeId.Parse("ns=3;s=Telemetry"), nodeId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RejectsMissingOrAmbiguousBrowsePaths(int matches)
    {
        IReadOnlyList<BrowseNode> children = Enumerable.Range(0, matches)
            .Select(index => new BrowseNode(NodeId.Parse($"ns=2;i={index + 1}"), "Plant01", NodeClass.Object))
            .ToArray();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpcUaTagDiscovery.ResolveStartAsync("Objects/Plant01", (_, _) => Task.FromResult(children),
                TestContext.Current.CancellationToken));

        Assert.Contains("OpcUa:BrowseStartNode", exception.Message);
    }

    private static BrowseNode Node(string name, NodeClass nodeClass)
        => new(NodeId.Parse($"ns=2;s={name}"), name, nodeClass);
}
