using Confluent.Kafka;
using Telemetrix.Transformer.Consuming;

namespace Telemetrix.Transformer.Tests;

public class ConsumerConfigTests
{
    [Fact]
    public void RawConsumerCannotAdvanceOffsetsAutomatically()
    {
        var config = RawBatchConsumer.CreateConfig(new KafkaOptions { BootstrapServers = "broker:9092" });

        Assert.Equal("broker:9092", config.BootstrapServers);
        Assert.Equal("transformer", config.GroupId);
        Assert.False(config.EnableAutoCommit);
        Assert.False(config.EnableAutoOffsetStore);
        Assert.Equal(AutoOffsetReset.Earliest, config.AutoOffsetReset);
        Assert.False(config.AllowAutoCreateTopics);
    }
}
