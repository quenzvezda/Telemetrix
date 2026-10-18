namespace Telemetrix.Transformer.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "ConnectionStrings";
    public string Telemetry { get; set; } = "";
}
