using System.Security.Cryptography;
using System.Text;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Publishing;
using BridgeKafka = Telemetrix.Edge.Bridge.Publishing.KafkaOptions;

// A deterministic, test-only source for the M4 kill-under-load review.
// Run against the isolated review stack, never against the normal local database.
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = deadline.Token;
var bootstrap = Environment.GetEnvironmentVariable("Kafka__BootstrapServers") ?? "kafka:9092";
var runId = Guid.Parse(Environment.GetEnvironmentVariable("M4_RUN_ID") ?? throw new InvalidOperationException("M4_RUN_ID is required."));
var batches = int.Parse(Environment.GetEnvironmentVariable("M4_BATCHES") ?? "2000");
var samples = int.Parse(Environment.GetEnvironmentVariable("M4_SAMPLES") ?? "10");
var delay = int.Parse(Environment.GetEnvironmentVariable("M4_DELAY_MS") ?? "10");
if (batches <= 0 || samples <= 0 || delay < 0) throw new InvalidOperationException("Invalid review load settings.");
var ids = Enumerable.Range(0, batches).Select(i => new Guid(SHA256.HashData(
    Encoding.UTF8.GetBytes($"{runId:D}:{i}"))[..16])).ToArray();
var command = args.Single();

if (command is "prepare" or "send")
{
    await new TopicInitializer(Options.Create(new BridgeKafka { BootstrapServers = bootstrap }),
        NullLogger<TopicInitializer>.Instance).StartAsync(ct);
    if (command == "prepare") return;
    using var producer = new ProducerBuilder<string, byte[]>(KafkaBatchPublisher.CreateConfig(
        new BridgeKafka { BootstrapServers = bootstrap })).Build();
    var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        .AddSeconds(BitConverter.ToUInt32(runId.ToByteArray()) % 31536000);
    for (var sequence = 0; sequence < batches; sequence++)
    {
        var batch = new TelemetryBatch(1, ids[sequence], "opcua", "opc.tcp://deterministic-review:50000",
            "Plant01", "Line01", "Pump01", start, Enumerable.Range(0, samples).Select(i =>
            new BatchSample("FlowRate", i % 80, start.AddMilliseconds((long)sequence * samples + i), start, 0)).ToArray());
        await producer.ProduceAsync(BridgeKafka.RawTopic, new Message<string, byte[]>
        {
            Key = batch.AssetId, Value = TelemetryJson.Serialize(batch)
        }, ct);
        if (delay > 0) await Task.Delay(delay, ct);
    }
    Console.WriteLine($"Sent {batches} batches / {checked(batches * samples)} distinct samples; run={runId:D}");
    return;
}

await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__Telemetry")
    ?? throw new InvalidOperationException("ConnectionStrings__Telemetry is required."));
await using var connection = await source.OpenConnectionAsync(ct);
if (command == "count")
{
    Console.WriteLine(await connection.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT count(*) FROM measurement WHERE batch_id = ANY(@Ids)", new { Ids = ids }, cancellationToken: ct)));
    return;
}
if (command != "verify") throw new InvalidOperationException("Use prepare, send, count or verify.");

using var inspector = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
{
    BootstrapServers = bootstrap, GroupId = "transformer", EnableAutoCommit = false, EnableAutoOffsetStore = false
}).Build();
var partitions = Enumerable.Range(0, 6).Select(p => new TopicPartition(BridgeKafka.RawTopic, new Partition(p))).ToArray();
while (true)
{
    var counts = (await connection.QueryAsync<(Guid BatchId, int Count)>(new CommandDefinition(
        "SELECT batch_id, count(*)::int FROM measurement WHERE batch_id = ANY(@Ids) GROUP BY batch_id",
        new { Ids = ids }, cancellationToken: ct))).ToDictionary(row => row.BatchId, row => row.Count);
    if (counts.Any(pair => pair.Value > samples)) throw new InvalidOperationException("Duplicate measurements detected.");
    var offsets = inspector.Committed(partitions, TimeSpan.FromSeconds(10));
    var drained = offsets.All(offset => Math.Max(0, offset.Offset.Value)
        == inspector.QueryWatermarkOffsets(offset.TopicPartition, TimeSpan.FromSeconds(10)).High.Value);
    if (ids.All(id => counts.TryGetValue(id, out var count) && count == samples) && drained)
    {
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM measurement", cancellationToken: ct));
        if (total != checked(batches * samples)) throw new InvalidOperationException("Unexpected measurements in isolated review database.");
        Console.WriteLine($"PASS: sent={batches * samples}, stored={total}, batches={counts.Count}, lag=0; run={runId:D}");
        return;
    }
    await Task.Delay(200, ct);
}
