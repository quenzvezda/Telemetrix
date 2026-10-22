using System.Threading.Channels;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

internal static class EdgeOutageReview
{
    private static readonly (string Asset, string Tag, double Value)[] Tags =
    [
        ("Pump01", "Running", 1), ("Pump01", "FlowRate", 10),
        ("Pump01", "DischargePressure", 2), ("Pump01", "MotorTemperature", 20),
        ("Pump01", "Vibration", 1), ("Tank01", "Level", 50), ("Tank01", "Temperature", 20)
    ];

    private static int Ticks => int.Parse(Environment.GetEnvironmentVariable("M5_TICKS") ?? "1840");

    public static async Task SendAsync(string bootstrap, Guid runId, CancellationToken ct)
    {
        var ticks = Ticks;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticks);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = bootstrap,
            ["Buffer:Path"] = Environment.GetEnvironmentVariable("Buffer__Path") ?? "/buffer/bridge.db",
            ["Batching:MaxSamples"] = "500", ["Batching:MaxAge"] = "00:00:05"
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<OpcUaOptions>(settings => settings.Endpoint = "opc.tcp://deterministic-review:50000");
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddSeconds(BitConverter.ToUInt32(runId.ToByteArray()) % 31536000);
        builder.Services.AddSingleton<ISourceAdapter>(new DefaultLoadSource(ticks, start));
        builder.Services.AddBridgePipeline(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync(ct);
        var worker = host.Services.GetServices<IHostedService>().OfType<OpcUaIngestionWorker>().Single();
        await worker.ExecuteTask!.WaitAsync(ct);
        var buffer = host.Services.GetRequiredService<SqliteBuffer>();
        while (buffer.Depth.Batches > 0) await Task.Delay(100, ct);
        await host.StopAsync(ct);
        Console.WriteLine($"M5_SOURCE_DONE: ticks={ticks}, sent={ticks * Tags.Length}, buffer=0");
    }

    public static async Task VerifyAsync(string bootstrap, CancellationToken ct)
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__Telemetry")
            ?? throw new InvalidOperationException("ConnectionStrings__Telemetry is required."));
        await using var connection = await source.OpenConnectionAsync(ct);
        using var inspector = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = bootstrap, GroupId = "transformer", EnableAutoCommit = false, EnableAutoOffsetStore = false
        }).Build();
        var partitions = Enumerable.Range(0, 6).Select(index =>
            new TopicPartition(KafkaOptions.RawTopic, new Partition(index))).ToArray();
        while (true)
        {
            var counts = (await connection.QueryAsync<(string Asset, string Tag, int Count)>(new CommandDefinition(
                """
                SELECT t.asset_id, t.name, count(*)::int
                FROM measurement m JOIN tag t ON t.tag_id=m.tag_id GROUP BY t.asset_id, t.name
                """, cancellationToken: ct))).ToDictionary(value => (value.Asset, value.Tag), value => value.Count);
            if (counts.Any(value => value.Value > Ticks)) throw new InvalidOperationException("Duplicate or unexpected measurements.");
            var complete = counts.Count == Tags.Length && Tags.All(tag =>
                counts.TryGetValue((tag.Asset, tag.Tag), out var count) && count == Ticks);
            var offsets = inspector.Committed(partitions, TimeSpan.FromSeconds(10));
            var drained = offsets.All(offset => Math.Max(0, offset.Offset.Value) ==
                inspector.QueryWatermarkOffsets(offset.TopicPartition, TimeSpan.FromSeconds(10)).High.Value);
            if (complete && drained)
            {
                var total = counts.Values.Sum();
                Console.WriteLine($"M5_PASS: sent={Ticks * Tags.Length}, stored={total}, tags={counts.Count}, lag=0");
                return;
            }
            await Task.Delay(200, ct);
        }
    }

    private sealed class DefaultLoadSource(int ticks, DateTimeOffset start) : ISourceAdapter
    {
        public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken ct)
        {
            for (var tick = 0; tick < ticks; tick++)
            {
                foreach (var (asset, tag, value) in Tags)
                    await writer.WriteAsync(new Sample("Plant01", "Line01", asset, tag, value,
                        start.AddSeconds(tick), start.AddSeconds(tick), 0), ct);
                if (tick == 0) Console.WriteLine("M5_SOURCE_READY: seven tags, one sample per tag per second");
                if (tick % 60 == 0) Console.WriteLine($"M5_SOURCE_PROGRESS: ticks={tick + 1}, sent={(tick + 1) * Tags.Length}");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
