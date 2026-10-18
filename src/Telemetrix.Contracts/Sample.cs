namespace Telemetrix.Contracts;

public sealed record Sample
{
    public Sample(
        string plant,
        string line,
        string asset,
        string tag,
        double? value,
        DateTimeOffset sourceTimestamp,
        DateTimeOffset serverTimestamp,
        uint statusCode)
    {
        Plant = plant;
        Line = line;
        Asset = asset;
        Tag = tag;
        Value = value;
        SourceTimestamp = sourceTimestamp.ToUniversalTime();
        ServerTimestamp = serverTimestamp.ToUniversalTime();
        StatusCode = statusCode;
    }

    public string Plant { get; }
    public string Line { get; }
    public string Asset { get; }
    public string Tag { get; }
    public double? Value { get; }
    public DateTimeOffset SourceTimestamp { get; }
    public DateTimeOffset ServerTimestamp { get; }
    public uint StatusCode { get; }
}
