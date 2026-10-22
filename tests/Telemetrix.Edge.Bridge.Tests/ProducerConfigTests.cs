using Confluent.Kafka;
using Telemetrix.Edge.Bridge.Publishing;

namespace Telemetrix.Edge.Bridge.Tests;

public class ProducerConfigTests
{
    [Fact]
    public void RawProducerRequiresDurableIdempotentOrderedDelivery()
    {
        var config = KafkaBatchPublisher.CreateConfig(new KafkaOptions { BootstrapServers = "broker:9092" });

        Assert.Equal("broker:9092", config.BootstrapServers);
        Assert.Equal(Acks.All, config.Acks);
        Assert.True(config.EnableIdempotence);
        Assert.Equal(5, config.MaxInFlight);
        Assert.Equal(5, config.LingerMs);
        Assert.Equal(CompressionType.Lz4, config.CompressionType);
        Assert.False(config.AllowAutoCreateTopics);
    }
}
