using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Telemetrix.Edge.Bridge;
using Telemetrix.MqttSim;
using Telemetrix.Transformer;

namespace Telemetrix.Integration.Tests;

[Collection("Observability integration")]
public class LoadProfileTests(ITestOutputHelper output)
{
    [Fact(Explicit = true)]
    public async Task RunConfiguredProfile()
    {
        var kind = Environment.GetEnvironmentVariable("M8_LOAD_KIND") ?? "latency";
        Assert.True(kind is "latency" or "throughput");
        var seconds = Setting("M8_LOAD_SECONDS", kind == "latency" ? 1800 : 900);
        var maxSamples = Setting("M8_BATCH_SAMPLES", 500);
        var age = Setting("M8_BATCH_SECONDS", 5);
        var batchQueueCapacity = Setting("M8_BATCH_QUEUE_CAPACITY", kind == "throughput" ? 1024 : 64);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 600));
        var ct = deadline.Token;
        var root = FindRepository();
        var directory = Path.Combine(Path.GetTempPath(), $"telemetrix-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        long received = 0, written = 0, deadLettered = 0, bufferDepth = 0;
        var lag = new ConcurrentDictionary<int, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, target) =>
        {
            if (instrument.Meter.Name is "Telemetrix.Bridge" or "Telemetrix.Transformer") target.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            switch (instrument.Name)
            {
                case "telemetrix.samples.received": Interlocked.Add(ref received, value); break;
                case "telemetrix.rows.written": Interlocked.Add(ref written, value); break;
                case "telemetrix.batches.deadlettered": Interlocked.Add(ref deadLettered, value); break;
                case "telemetrix.buffer.depth": Interlocked.Exchange(ref bufferDepth, value); break;
                case "telemetrix.consumer.lag":
                    foreach (var tag in tags) if (tag.Key == "partition") lag[(int)tag.Value!] = value;
                    break;
            }
        });
        listener.Start();
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        await RollupMigrationTests.MigrateAsync(environment.Source, ct);
        var lines = kind == "throughput" ? 143 : 1;
        if (lines > 1) await SeedCatalogAsync(environment, lines, ct);
        await using var mqtt = new MqttFixture();
        await using var plant = new PlantSimFixture();
        WebApplication? mqttSimulator = null;
        var arguments = new List<string>
        {
            "--Http:Port=0", "--Telemetry:Prometheus:Enabled=true", "--Logging:LogLevel:Default=Warning",
            $"--Kafka:BootstrapServers={environment.Kafka.GetBootstrapAddress()}",
            $"--Buffer:Path={Path.Combine(directory, "bridge.db")}",
            $"--Ingestion:BatchQueueCapacity={batchQueueCapacity}",
            $"--Batching:MaxSamples={maxSamples}", $"--Batching:MaxAge={TimeSpan.FromSeconds(age):c}"
        };
        if (kind == "throughput")
        {
            await mqtt.Broker.StartAsync(ct);
            arguments.AddRange(["--Source:Kind=Mqtt", $"--Source:Mqtt:Host={mqtt.Host}", $"--Source:Mqtt:Port={mqtt.Port}",
                $"--Source:Mqtt:ClientId=load-{Guid.NewGuid():N}"]);
            mqttSimulator = MqttSimApplication.Build(["--Http:Port=0", $"--MqttSim:Host={mqtt.Host}", $"--MqttSim:Port={mqtt.Port}",
                "--MqttSim:LineCount=143", "--MqttSim:TickInterval=00:00:00.200", "--MqttSim:MaxInFlight=64", "--Logging:LogLevel:Default=Warning"]);
        }
        else
        {
            await plant.StartAsync(ct);
            arguments.AddRange(["--environment=PlantSim", $"--contentRoot={Path.Combine(AppContext.BaseDirectory, "config")}",
                $"--OpcUa:Endpoint={plant.Endpoint}"]);
        }
        try
        {
            await using var bridge = BridgeApplication.Build(arguments.ToArray());
            await using var transformer = TransformerApplication.Build(["--Http:Port=0", "--Telemetry:Prometheus:Enabled=true",
                "--Logging:LogLevel:Default=Warning", $"--Kafka:BootstrapServers={environment.Kafka.GetBootstrapAddress()}",
                $"--ConnectionStrings:Telemetry={environment.Database.GetConnectionString()}"]);
            await transformer.StartAsync(ct);
            await bridge.StartAsync(ct);
            if (mqttSimulator is not null) await mqttSimulator.StartAsync(ct);
            try
            {
                while (Interlocked.Read(ref received) == 0) await Task.Delay(100, ct);
                await Task.Delay(TimeSpan.FromSeconds(Setting("M8_LOAD_WARMUP_SECONDS", 30)), ct);
                using var http = new HttpClient();
                Assert.Contains("telemetrix_samples_received", await http.GetStringAsync(WebHostTests.Address(bridge) + "/metrics", ct));
                var started = DateTimeOffset.UtcNow;
                var initialReceived = Interlocked.Read(ref received);
                var initialWritten = Interlocked.Read(ref written);
                var initialDeadLettered = Interlocked.Read(ref deadLettered);
                var publisher = mqttSimulator?.Services.GetRequiredService<MqttSimulator>();
                var initialPublished = publisher?.PublishedSamples ?? 0;
                var watch = Stopwatch.StartNew();
                var observations = new List<LagObservation>();
                output.WriteLine($"M8_LOAD_START: {kind}, {maxSamples}/{age}s, duration={seconds}s, source={lines} lines.");
                while (watch.Elapsed.TotalSeconds < seconds)
                {
                    listener.RecordObservableInstruments();
                    observations.Add(new(watch.Elapsed.TotalSeconds, lag.Values.Sum()));
                    if (observations.Count % 6 == 1)
                        output.WriteLine($"M8_LOAD_PROGRESS: {watch.Elapsed.TotalSeconds:F0}/{seconds}s, published={publisher?.PublishedSamples-initialPublished}, received={Interlocked.Read(ref received)-initialReceived}, written={Interlocked.Read(ref written)-initialWritten}, lag={lag.Values.Sum()}, buffer={Interlocked.Read(ref bufferDepth)}");
                    var remaining = TimeSpan.FromSeconds(seconds) - watch.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5), ct);
                }
                var measuredSeconds = watch.Elapsed.TotalSeconds;
                var ended = DateTimeOffset.UtcNow;
                var accepted = Interlocked.Read(ref received) - initialReceived;
                var stored = Interlocked.Read(ref written) - initialWritten;
                var published = publisher is null ? accepted : publisher.PublishedSamples - initialPublished;
                listener.RecordObservableInstruments();
                observations.Add(new(measuredSeconds, lag.Values.Sum()));
                if (publisher is not null)
                {
                    await publisher.CompleteAsync(ct);
                    var sourceDrain = Stopwatch.StartNew();
                    while (Interlocked.Read(ref received) < publisher.PublishedSamples && sourceDrain.Elapsed < TimeSpan.FromSeconds(30))
                        await Task.Delay(100, ct);
                }
                await bridge.StopAsync(ct);
                var finalReceived = Interlocked.Read(ref received);
                var expectedMeasurements = publisher?.PublishedSamples ?? finalReceived;
                var storageDrain = Stopwatch.StartNew();
                while (await environment.CountAsync(ct) < expectedMeasurements && storageDrain.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(200, ct);
                var finalStored = await environment.CountAsync(ct);
                var drained = expectedMeasurements == finalStored;
                if (publisher is not null && !drained)
                {
                    var logs = await mqtt.Broker.GetLogsAsync(ct: ct);
                    foreach (var line in (logs.Stdout + logs.Stderr).Split('\n').Where(line => line.Contains("drop", StringComparison.OrdinalIgnoreCase)).TakeLast(10))
                        output.WriteLine("M8_BROKER_DIAGNOSTIC: " + line);
                }
                var deadLetters = Interlocked.Read(ref deadLettered) - initialDeadLettered;
                await using var connection = await environment.Source.OpenConnectionAsync(ct);
                var p95 = await connection.ExecuteScalarAsync<double>(new CommandDefinition("""
                    SELECT coalesce(extract(epoch FROM percentile_cont(0.95) WITHIN GROUP (ORDER BY ingested_at-time))*1000,0)
                    FROM measurement WHERE time>=@Start AND time<@End
                    """, new { Start = started, End = ended }, cancellationToken: ct));
                var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM measurement WHERE time>=@Start AND time<@End", new { Start = started, End = ended }, cancellationToken: ct));
                var firstMean = observations.Where(sample => sample.Seconds <= 60).Average(sample => (double)sample.Batches);
                var lastMean = observations.Where(sample => sample.Seconds >= Math.Max(0, measuredSeconds-60)).Average(sample => (double)sample.Batches);
                var slope = Slope(observations);
                var rate = Math.Min(accepted,published) / measuredSeconds;
                var fullDuration = seconds >= (kind == "latency" ? 1800 : 900);
                var passed = drained && deadLetters == 0 && count > 0 && (kind == "latency" ? p95 < 10000 : rate >= 5000 && (!fullDuration || slope <= 0.01 && lastMean <= firstMean + 6));
                var report = new
                {
                    kind, maxSamples, batchAgeSeconds = age, lines, tickMilliseconds = kind == "throughput" ? 200 : 1000,
                    maxInFlight = kind == "throughput" ? 64 : 1, rawPartitions = 6, batchQueueCapacity,
                    startedAt = started, endedAt = ended, measuredSeconds, acceptanceDuration = fullDuration,
                    samplesReceived = accepted, measurementsWritten = stored, actualSamplesPerSecond = rate,
                    samplesPublished = published, finalPublishedSamples = publisher?.PublishedSamples,
                    drainedBeforeDeadline = drained,
                    writtenPerSecond = stored/measuredSeconds, sourceWindowMeasurements = count, p95Milliseconds = p95,
                    deadLetteredBatches = deadLetters, finalAcceptedSamples = finalReceived,
                    finalStoredMeasurements = finalStored, lagSlopeBatchesPerSecond = slope,
                    firstMinuteMeanLag = firstMean, lastMinuteMeanLag = lastMean, maxLag = observations.Max(sample => sample.Batches),
                    lag = observations, passed, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription, logicalProcessors = Environment.ProcessorCount
                };
                var path = Environment.GetEnvironmentVariable("M8_LOAD_REPORT") ?? Path.Combine(root, "artifacts", $"m8-{kind}-{maxSamples}-{age}-{started:yyyyMMddHHmmss}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
                output.WriteLine($"M8_LOAD_RESULT: rate={rate:F3}/s, p95={p95:F3}ms, lag slope={slope:F6}, first/last mean={firstMean:F3}/{lastMean:F3}, deadletters={deadLetters}, passed={passed}, fullDuration={fullDuration}, report={path}");
                output.WriteLine($"M8_SOURCE_COUNTS: published={published}, received={accepted}, written={stored}, total published={publisher?.PublishedSamples}, total received={finalReceived}");
                Assert.True(passed, $"Load profile failed; inspect the saved report: {path}");
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (mqttSimulator is not null) await mqttSimulator.StopAsync(cleanup.Token);
                await bridge.StopAsync(cleanup.Token);
                await transformer.StopAsync(cleanup.Token);
            }
        }
        finally
        {
            if (mqttSimulator is not null) await mqttSimulator.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static int Setting(string name, int fallback) => int.Parse(Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    private static double Slope(IReadOnlyList<LagObservation> samples)
    {
        var x = samples.Average(sample => sample.Seconds);
        var y = samples.Average(sample => (double)sample.Batches);
        var divisor = samples.Sum(sample => Math.Pow(sample.Seconds-x,2));
        return divisor == 0 ? 0 : samples.Sum(sample => (sample.Seconds-x)*(sample.Batches-y))/divisor;
    }

    private static async Task SeedCatalogAsync(DeliveryTestEnvironment environment, int lines, CancellationToken ct)
    {
        await using var connection = await environment.Source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO asset(asset_id,plant_id,line_id,asset_type)
            SELECT base.asset_id||'_'||lpad(i::text,4,'0'),base.plant_id,base.line_id||'_'||lpad(i::text,4,'0'),base.asset_type
            FROM asset base CROSS JOIN generate_series(2,@Lines) i WHERE base.asset_id IN ('Pump01','Tank01');
            INSERT INTO tag(asset_id,name,unit,min_valid,max_valid)
            SELECT a.asset_id,t.name,t.unit,t.min_valid,t.max_valid FROM asset a
            JOIN tag t ON t.asset_id=split_part(a.asset_id,'_',1)
            WHERE a.asset_id LIKE 'Pump01_%' OR a.asset_id LIKE 'Tank01_%';
            """, new { Lines = lines }, cancellationToken: ct));
    }

    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName,"Telemetrix.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Cannot find the load profile root.");
    }
    private sealed record LagObservation(double Seconds, long Batches);
}
