namespace Telemetrix.Contracts;

public sealed record BatchSample
{
    public BatchSample(
        string tag,
        double? value,
        DateTimeOffset sourceTimestamp,
        DateTimeOffset serverTimestamp,
        uint statusCode)
    {
        Tag = tag;
        Value = value;
        SourceTimestamp = sourceTimestamp.ToUniversalTime();
        ServerTimestamp = serverTimestamp.ToUniversalTime();
        StatusCode = statusCode;
    }

    public string Tag { get; }
    public double? Value { get; }
    public DateTimeOffset SourceTimestamp { get; }
    public DateTimeOffset ServerTimestamp { get; }
    public uint StatusCode { get; }
}
