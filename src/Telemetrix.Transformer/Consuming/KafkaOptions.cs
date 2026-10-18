namespace Telemetrix.Transformer.Consuming;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public const string RawTopic = "plant.telemetry.raw";
    public const string DeadLetterTopic = "plant.telemetry.dlq";
    public const string ConsumerGroup = "transformer";

    public string BootstrapServers { get; set; } = "localhost:29092";
}
