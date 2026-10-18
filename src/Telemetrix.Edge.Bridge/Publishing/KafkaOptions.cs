namespace Telemetrix.Edge.Bridge.Publishing;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public const string RawTopic = "plant.telemetry.raw";

    public string BootstrapServers { get; set; } = "localhost:29092";
}
