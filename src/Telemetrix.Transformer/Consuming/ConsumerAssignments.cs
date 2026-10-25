using Confluent.Kafka;

namespace Telemetrix.Transformer.Consuming;

public sealed class ConsumerAssignments
{
    private readonly Lock gate = new();
    private TopicPartition[] assigned = [];
    public TopicPartition[] Snapshot() { lock (gate) return assigned.ToArray(); }
    public void Set(IEnumerable<TopicPartition> partitions) { lock (gate) assigned = partitions.ToArray(); }
}
