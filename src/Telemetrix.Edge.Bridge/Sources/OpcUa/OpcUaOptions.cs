namespace Telemetrix.Edge.Bridge.Sources.OpcUa;

public sealed class OpcUaOptions
{
    public const string SectionName = "OpcUa";

    public string Plant { get; set; } = "";
    public string Line { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string SecurityMode { get; set; } = "None";
    public int SamplingIntervalMs { get; set; } = 500;
    public int PublishingIntervalMs { get; set; } = 1000;
    public uint QueueSize { get; set; } = 10;
    public List<OpcUaTagOptions> Tags { get; set; } = [];
    public string? BrowseStartNode { get; set; }
}

public sealed class OpcUaTagOptions
{
    public string Asset { get; set; } = "";
    public string Tag { get; set; } = "";
    public string NodeId { get; set; } = "";
}
