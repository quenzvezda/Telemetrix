namespace Telemetrix.Edge.Bridge.Buffer;

public sealed class BufferOptions
{
    public const string SectionName = "Buffer";
    public string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Telemetrix", "buffer", "bridge.db");
    public long MaxBytes { get; set; } = 64 * 1024 * 1024;
}
