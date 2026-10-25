using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Integration.Tests;

public class PlantEventStorageTests
{
    [Fact]
    public async Task EventsAreStoredBeforeCommitAndReplayIsIdempotent()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "migrations", "002_plant_events.sql"), ct);
        await using (var command = environment.Source.CreateCommand(migration))
        {
            await command.ExecuteNonQueryAsync(ct);
            await command.ExecuteNonQueryAsync(ct);
        }
        using (var admin = environment.Admin())
            await admin.CreateTopicsAsync([new TopicSpecification { Name = PlantEventConsumer.Topic, NumPartitions = 3, ReplicationFactor = 1 }]).WaitAsync(ct);
        var settings = Options.Create(new KafkaOptions { BootstrapServers = environment.Kafka.GetBootstrapAddress() });
        var now = DateTimeOffset.UtcNow;
        PlantEvent[] events =
        [
            new(1, Guid.NewGuid(), PlantEventType.ConnectionLost, "Plant01", "Line01", "Pump01", "opc.tcp://test", now),
            new(1, Guid.NewGuid(), PlantEventType.ConnectionRestored, "Plant01", "Line01", "Pump01", "opc.tcp://test", now.AddSeconds(60)),
            new(1, Guid.NewGuid(), PlantEventType.Gap, "Plant01", "Line01", "Pump01", "opc.tcp://test", now.AddSeconds(60), now, now.AddSeconds(60), "source-unavailable")
        ];
        using var consumer = new PlantEventConsumer(settings, new PlantEventWriter(environment.Source), ResiliencePipeline.Empty,
            NullLogger<PlantEventConsumer>.Instance);
        await consumer.StartAsync(ct);
        try
        {
            foreach (var item in events.Concat(events))
                await environment.Producer.ProduceAsync(new TopicPartition(PlantEventConsumer.Topic, new Partition(0)),
                    new Message<string, byte[]> { Key = item.AssetId, Value = TelemetryJson.Serialize(item) }, ct);
            var config = RawBatchConsumer.CreateConfig(settings.Value);
            config.GroupId = PlantEventConsumer.Group;
            using var inspector = new ConsumerBuilder<string, byte[]>(config).Build();
            var partition = new TopicPartition(PlantEventConsumer.Topic, new Partition(0));
            while (true)
            {
                try
                {
                    if (inspector.Committed([partition], TimeSpan.FromSeconds(2)).Single().Offset.Value == 6) break;
                }
                catch (KafkaException exception) when (exception.Error.Code is ErrorCode.GroupLoadInProgress or ErrorCode.Local_TimedOut) { }
                await Task.Delay(50, ct);
            }
            await using var connection = await environment.Source.OpenConnectionAsync(ct);
            Assert.Equal(3, await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM plant_event", cancellationToken: ct)));
            Assert.Equal(60, await connection.ExecuteScalarAsync<double>(new CommandDefinition(
                "SELECT extract(epoch FROM gap_end-gap_start) FROM plant_event WHERE event_type='Gap'", cancellationToken: ct)));
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM plant_event WHERE event_type='ConnectionLost'", cancellationToken: ct)));
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM plant_event WHERE event_type='ConnectionRestored'", cancellationToken: ct)));
        }
        finally { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await consumer.StopAsync(cleanup.Token); }
    }
}
