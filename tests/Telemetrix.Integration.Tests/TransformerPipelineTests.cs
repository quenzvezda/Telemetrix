using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Telemetrix.Contracts;
using Telemetrix.Transformer;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace Telemetrix.Integration.Tests;

public class TransformerPipelineTests
{
    [Theory]
    [InlineData(TagResolutionOptions.DeadLetter)]
    [InlineData(TagResolutionOptions.Create)]
    public async Task RealHostStoresRawBatchesWithQualityAndAppliesTheUnknownTagPolicy(string policy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var kafka = new KafkaBuilder("apache/kafka:4.3.1")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false").Build();
        await using var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17").Build();
        await Task.WhenAll(kafka.StartAsync(ct), database.StartAsync(ct));
        await using var source = NpgsqlDataSource.Create(database.GetConnectionString());
        await using (var connection = await source.OpenConnectionAsync(ct))
            await connection.ExecuteAsync(new CommandDefinition(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "sql", "init.sql"), ct), cancellationToken: ct));
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.GetBootstrapAddress() }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification
        {
            Name = KafkaOptions.RawTopic, NumPartitions = 6, ReplicationFactor = 1
        }]).WaitAsync(ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = kafka.GetBootstrapAddress(),
            ["ConnectionStrings:Telemetry"] = database.GetConnectionString(),
            ["TagResolution:UnknownTagPolicy"] = policy
        });
        builder.Services.AddTransformerPipeline(builder.Configuration);
        using var host = builder.Build();
        try
        {
            await host.StartAsync(ct);
            var timestamp = new DateTimeOffset(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);
            var batch = new TelemetryBatch(1, Guid.NewGuid(), "opcua", "opc.tcp://test:50000",
                "Plant01", "Line01", "Pump01", timestamp.AddHours(1),
                [new("Running", 1, timestamp, timestamp.AddMinutes(1), 0),
                 new("FlowRate", 52.8, timestamp, timestamp.AddMinutes(1), 0),
                 new("MotorTemperature", 500, timestamp, timestamp.AddMinutes(1), 0),
                 new("DischargePressure", 3, timestamp, timestamp.AddMinutes(1), 0x40000000),
                 new("Vibration", null, timestamp, timestamp.AddMinutes(1), 0),
                 new("FlowRate", 25, timestamp.AddSeconds(1), timestamp.AddMinutes(1), 0x80000000),
                 new("Torque", 42, timestamp, timestamp.AddMinutes(1), 0)]);
            using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
            {
                BootstrapServers = kafka.GetBootstrapAddress(), Acks = Acks.All, EnableIdempotence = true
            }).Build();
            await producer.ProduceAsync(KafkaOptions.RawTopic,
                new Message<string, byte[]> { Key = batch.AssetId, Value = TelemetryJson.Serialize(batch) }, ct);

            var creates = policy == TagResolutionOptions.Create;
            var expected = creates ? 7 : 6;
            await using var connection = await source.OpenConnectionAsync(ct);
            while (await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM measurement WHERE batch_id = @BatchId", new { batch.BatchId }, cancellationToken: ct)) < expected)
                await Task.Delay(100, ct);

            await using (var command = new NpgsqlCommand("""
                SELECT tag.name, measurement.time, measurement.value, measurement.quality, measurement.batch_id
                FROM measurement JOIN tag USING (tag_id) ORDER BY measurement.time, tag.name
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                var rows = 0;
                while (await reader.ReadAsync(ct))
                {
                    rows++;
                    var tag = reader.GetString(0);
                    var time = reader.GetFieldValue<DateTimeOffset>(1);
                    var sample = Assert.Single(batch.Samples, sample => sample.Tag == tag && sample.SourceTimestamp == time);
                    Assert.Equal(sample.Value, reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2));
                    var quality = tag switch
                    {
                        "MotorTemperature" or "Vibration" => 2,
                        "DischargePressure" => 1,
                        "FlowRate" when time > timestamp => 2,
                        _ => 0
                    };
                    Assert.Equal((short)quality, reader.GetInt16(3));
                    Assert.Equal(batch.BatchId, reader.GetGuid(4));
                    Assert.Equal(TimeSpan.Zero, time.Offset);
                }
                Assert.Equal(expected, rows);
            }
            Assert.Equal(creates ? 1 : 0, await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT count(*) FROM tag WHERE asset_id = 'Pump01' AND name = 'Torque'
                AND unit IS NULL AND min_valid IS NULL AND max_valid IS NULL
                """, cancellationToken: ct)));
            using var dlq = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = kafka.GetBootstrapAddress(), GroupId = Guid.NewGuid().ToString(), EnableAutoCommit = false
            }).Build();
            var partition = new TopicPartition(KafkaOptions.DeadLetterTopic, new Partition(0));
            if (creates)
                Assert.Equal(0, dlq.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10)).High.Value);
            else
            {
                dlq.Assign(new TopicPartitionOffset(partition, Offset.Beginning));
                var rejected = await Task.Run(() => dlq.Consume(ct), ct);
                Assert.Equal(batch.AssetId, rejected.Message.Key);
                Assert.Equal("unknown-tag", Encoding.UTF8.GetString(rejected.Message.Headers.GetLastBytes("dlq.reason")));
                var restored = TelemetryJson.Deserialize(rejected.Message.Value);
                Assert.Equal(batch.BatchId, restored.BatchId);
                Assert.Equal(batch.Samples[^1], Assert.Single(restored.Samples));
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await host.StopAsync(cleanup.Token);
        }
    }
}
