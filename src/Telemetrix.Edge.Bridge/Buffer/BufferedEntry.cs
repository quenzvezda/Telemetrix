namespace Telemetrix.Edge.Bridge.Buffer;

public sealed record BufferedEntry(string Topic, string AssetKey, byte[] Payload,
    int SampleCount, DateTimeOffset CreatedAt, long Sequence = 0);

public readonly record struct BufferDepth(long Batches, long Samples, long Bytes);
