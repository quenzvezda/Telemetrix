using Opc.Ua;
using Opc.Ua.Client;

namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

internal static class OpcUaTagDiscovery
{
    public static async Task<IReadOnlyList<OpcUaTagOptions>> DiscoverAsync(
        ISession session, string start, CancellationToken cancellationToken)
    {
        var browser = new Browser(session, new BrowserOptions
        {
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (int)(NodeClass.Object | NodeClass.Variable),
            ResultMask = (uint)BrowseResultMask.All
        }) { ContinueUntilDone = true };

        async Task<IReadOnlyList<BrowseNode>> BrowseAsync(NodeId nodeId, CancellationToken ct)
        {
            var references = await browser.BrowseAsync(nodeId, ct);
            List<BrowseNode> children = [];
            foreach (var reference in references)
            {
                if (reference.NodeId.ServerIndex == 0)
                {
                    children.Add(new BrowseNode(
                        ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris),
                        reference.BrowseName.Name ?? throw new InvalidOperationException("A discovered node has no browse name."),
                        reference.NodeClass));
                }
            }

            return children;
        }

        var startId = await ResolveStartAsync(start, BrowseAsync, cancellationToken);
        var startNode = await session.ReadNodeAsync(startId, cancellationToken);
        return await DiscoverAsync(new BrowseNode(startId,
            startNode.BrowseName.Name ?? throw new InvalidOperationException("OpcUa:BrowseStartNode has no browse name."), startNode.NodeClass),
            BrowseAsync, cancellationToken);
    }

    internal static async Task<NodeId> ResolveStartAsync(string start,
        Func<NodeId, CancellationToken, Task<IReadOnlyList<BrowseNode>>> browse,
        CancellationToken cancellationToken)
    {
        if (!start.StartsWith("Objects/", StringComparison.Ordinal) && start != "Objects")
        {
            return NodeId.Parse(start);
        }

        var current = ObjectIds.ObjectsFolder;
        foreach (var name in start.Split('/').Skip(1))
        {
            var matches = (await browse(current, cancellationToken))
                .Where(child => child.Name == name).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"OpcUa:BrowseStartNode '{start}' must resolve uniquely; '{name}' has {matches.Length} matches.");
            }

            current = matches[0].NodeId;
        }

        return current;
    }

    internal static async Task<IReadOnlyList<OpcUaTagOptions>> DiscoverAsync(BrowseNode start,
        Func<NodeId, CancellationToken, Task<IReadOnlyList<BrowseNode>>> browse,
        CancellationToken cancellationToken)
    {
        List<OpcUaTagOptions> tags = [];
        var visited = new HashSet<NodeId> { start.NodeId };
        var pending = new Queue<(NodeId NodeId, string Asset)>();
        pending.Enqueue((start.NodeId, start.Name));
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in await browse(current.NodeId, cancellationToken))
            {
                if (!visited.Add(child.NodeId))
                {
                    continue;
                }

                if (child.NodeClass == NodeClass.Variable)
                {
                    tags.Add(new OpcUaTagOptions
                    {
                        Asset = current.Asset,
                        Tag = child.Name,
                        NodeId = child.NodeId.ToString()
                    });
                    pending.Enqueue((child.NodeId, current.Asset));
                }
                else if (child.NodeClass == NodeClass.Object)
                {
                    pending.Enqueue((child.NodeId, child.Name));
                }
            }
        }

        return tags;
    }

    internal sealed record BrowseNode(NodeId NodeId, string Name, NodeClass NodeClass);
}
