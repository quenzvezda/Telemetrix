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
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using DotNet.Testcontainers.Networks;

namespace Telemetrix.Integration.Tests;

internal sealed class DeliveryTestEnvironment : IAsyncDisposable
{
    public KafkaContainer Kafka { get; }
    public PostgreSqlContainer Database { get; }

    public DeliveryTestEnvironment(INetwork? network = null)
    {
        var kafka = new KafkaBuilder("apache/kafka:4.3.1")
        // Interpret the copied script instead of exec'ing a file whose write handle may still be open.
        .WithCommand("while [ ! -f /testcontainers.sh ]; do sleep 0.1; done; /bin/sh /testcontainers.sh")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false");
        var database = new PostgreSqlBuilder("timescale/timescaledb:2.30.2-pg17");
        if (network is not null)
        {
            kafka = kafka.WithNetwork(network).WithNetworkAliases("kafka");
            database = database.WithNetwork(network).WithNetworkAliases("timescaledb");
        }
        Kafka = kafka.Build();
        Database = database.Build();
    }
    public NpgsqlDataSource Source { get; private set; } = null!;
    public IProducer<string, byte[]> Producer { get; private set; } = null!;

    public async Task StartAsync(CancellationToken ct)
    {
        await Task.WhenAll(Kafka.StartAsync(ct), Database.StartAsync(ct));
        Source = NpgsqlDataSource.Create(Database.GetConnectionString());
        await using var connection = await Source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "sql", "init.sql"), ct), cancellationToken: ct));
        using var admin = Admin();
        await admin.CreateTopicsAsync([new TopicSpecification
        {
            Name = KafkaOptions.RawTopic, NumPartitions = 6, ReplicationFactor = 1
        }]).WaitAsync(ct);
        Producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = Kafka.GetBootstrapAddress(), Acks = Acks.All, EnableIdempotence = true
        }).Build();
    }

    public IHost Host(Action<IServiceCollection>? configure = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = Kafka.GetBootstrapAddress(),
            ["ConnectionStrings:Telemetry"] = Database.GetConnectionString()
        });
        builder.Services.AddTransformerPipeline(builder.Configuration);
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    public IAdminClient Admin() => new AdminClientBuilder(new AdminClientConfig
        { BootstrapServers = Kafka.GetBootstrapAddress() }).Build();

    public Task<DeliveryResult<string, byte[]>> ProduceAsync(TelemetryBatch batch, int partition, CancellationToken ct) =>
        Producer.ProduceAsync(new TopicPartition(KafkaOptions.RawTopic, new Partition(partition)),
            new Message<string, byte[]> { Key = batch.AssetId, Value = TelemetryJson.Serialize(batch) }, ct);

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = await Source.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM measurement", cancellationToken: ct));
    }

    public async Task WaitForCountAsync(int count, CancellationToken ct)
    {
        while (await CountAsync(ct) < count) await Task.Delay(50, ct);
        Assert.Equal(count, await CountAsync(ct));
    }

    public async Task WaitForOffsetsAsync(long[] offsets, CancellationToken ct)
    {
        using var inspector = new ConsumerBuilder<string, byte[]>(RawBatchConsumer.CreateConfig(
            new KafkaOptions { BootstrapServers = Kafka.GetBootstrapAddress() })).Build();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var committed = inspector.Committed(Enumerable.Range(0, offsets.Length)
                    .Select(p => new TopicPartition(KafkaOptions.RawTopic, new Partition(p))), TimeSpan.FromSeconds(10));
                if (committed.All(p => p.Offset.Value == offsets[p.Partition.Value])) return;
            }
            catch (KafkaException exception) when (exception.Error.Code is ErrorCode.GroupLoadInProgress
                or ErrorCode.GroupCoordinatorNotAvailable or ErrorCode.NotCoordinatorForGroup or ErrorCode.Local_TimedOut)
            {
                // A fresh broker can move the coordinator while the inspector discovers it.
                // Keep the same deadline and exact offset assertion; permanent errors still fail.
                Console.WriteLine("Offset inspection readiness: {0}", exception.Error.Code);
            }
            await Task.Delay(50, ct);
        }
    }

    public async Task WaitForMembersAsync(int count, CancellationToken ct)
    {
        using var admin = Admin();
        await ConsumerGroupReadiness.WaitForMembersAsync(admin, count, ct);
    }

    public static TelemetryBatch Batch(int sequence, int samples = 10)
    {
        var time = new DateTimeOffset(2026, 10, 22, 5, 0, 0, TimeSpan.Zero).AddSeconds(sequence * samples);
        return new(1, Guid.NewGuid(), "opcua", "opc.tcp://test:50000", "Plant01", "Line01", "Pump01", time,
            Enumerable.Range(0, samples).Select(i => new BatchSample("FlowRate", i, time.AddSeconds(i), time, 0)).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        Producer?.Dispose();
        if (Source is not null) await Source.DisposeAsync();
        await Database.DisposeAsync();
        await Kafka.DisposeAsync();
    }
}
