using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Sources.OpcUa;
using Telemetrix.Edge.Bridge.Publishing;
using System.Collections.Concurrent;
using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Integration.Tests;

public class OpcUaRecoveryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"telemetrix-source-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task SamplesResumeAfterInterruptionsWithOrderedEventsAndGaps(bool browse,
        bool capacityPause, bool initiallyUnavailable)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2), TimeProvider.System);
        using var testCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);
        var ct = testCancellation.Token;
        // Pin a free host port: Docker may reassign an automatic binding after stop/start.
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var hostPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        await using var container = new ContainerBuilder("mcr.microsoft.com/iotedge/opc-plc:2.15.11")
            .WithPortBinding(hostPort, 50000)
            .WithCommand("--pn=50000", "--autoaccept", "--ut")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(50000)
                .UntilMessageIsLogged("OPC UA Server started"))
            .Build();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.Development.json"))
            .Build();
        var options = configuration.GetSection("OpcUa").Get<OpcUaOptions>()!;
        options.Endpoint = new UriBuilder("opc.tcp", container.Hostname, hostPort).Uri.ToString();
        if (browse)
        {
            options.BrowseStartNode = "Objects/OpcPlc/Telemetry";
        }

        var expectedTags = options.Tags.Select(tag => browse
            ? tag.NodeId.Split(";s=")[1]
            : tag.Tag).ToHashSet();
        var logger = new RecoveryLog(output);
        using var services = new ServiceCollection().AddLogging(logging => logging.AddProvider(logger)).BuildServiceProvider();
        var events = new CapturingEvents();
        using var buffer = new SqliteBuffer(Options.Create(new BufferOptions
        {
            Path = Path.Combine(directory, "bridge.db"), MaxBytes = 100
        }));
        using var tracker = new PlantEventTracker(Options.Create(options), events, TimeProvider.System);
        var adapter = new OpcUaSourceAdapter(Options.Create(options),
            new ServiceProviderTelemetryContext(services), TimeProvider.System, logger,
            buffer: capacityPause ? buffer : null, eventTracker: tracker);
        var channel = Channel.CreateBounded<Sample>(256);
        using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!initiallyUnavailable) await container.StartAsync(ct);
        var running = adapter.RunAsync(channel.Writer, sourceCancellation.Token);
        try
        {
            if (initiallyUnavailable)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                Assert.False(running.IsCompleted);
                await container.StartAsync(ct);
            }
            await ObserveAllTagsAsync(channel.Reader, running, expectedTags, DateTimeOffset.MinValue, ct);
            Assert.Empty(events.Values);
            for (var restart = 0; restart < 2; restart++)
            {
                logger.Clear();
                if (capacityPause)
                {
                    Assert.True(buffer.TryAppend(new BufferedEntry(KafkaOptions.RawTopic, "Pump01", new byte[80],
                        1, DateTimeOffset.UtcNow)));
                    await logger.WaitForAsync("OPC UA subscription stopping for buffer-capacity", running, ct);
                    var expectedLost = (restart + 1) * options.Tags.Select(tag => tag.Asset).Distinct().Count();
                    while (events.Values.Count(value => value.Type == PlantEventType.ConnectionLost) < expectedLost)
                        await Task.Delay(50, ct);
                    while (channel.Reader.TryRead(out _)) { }
                    await Task.Delay(500, ct);
                    Assert.False(channel.Reader.TryRead(out _));
                    buffer.DeleteAcknowledged(buffer.ReadOldest()!);
                }
                else
                {
                    await container.StopAsync(ct);
                    await container.StartAsync(ct);
                }
                Assert.Equal(options.Endpoint, new UriBuilder("opc.tcp", container.Hostname,
                    container.GetMappedPublicPort(50000)).Uri.ToString());
                var restartCompleted = TimeProvider.System.GetUtcNow();

                // A server restart creates fresh source values, so queued old samples cannot pass.
                // At capacity the server keeps running: an unchanged tag may legitimately retain
                // its earlier source timestamp. The drained/empty channel above proves collection
                // stopped; requiring every tag after resume proves the subscription was rebuilt.
                using var recoveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recoveryDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                await ObserveAllTagsAsync(channel.Reader, running, expectedTags,
                    capacityPause ? DateTimeOffset.MinValue : restartCompleted, recoveryDeadline.Token);
                await logger.WaitForAsync(capacityPause ? "OPC UA subscription created" : "OPC UA session restored",
                    running, recoveryDeadline.Token);
                while (events.Values.Count(value => value.Type == PlantEventType.Gap) <
                       events.Values.Count(value => value.Type == PlantEventType.ConnectionRestored))
                    await Task.Delay(50, recoveryDeadline.Token);
                var elapsed = TimeProvider.System.GetUtcNow() - restartCompleted;
                Assert.True(elapsed < TimeSpan.FromSeconds(30));
                output.WriteLine($"NFR-04 restart {restart + 1}: all tags and gaps resumed in {elapsed.TotalSeconds:F3} s");
                Assert.NotEmpty(events.Values);
                foreach (var asset in events.Values.GroupBy(value => value.AssetId))
                {
                    var transitions = asset.ToArray();
                    Assert.Equal(Enumerable.Range(0, restart + 1).SelectMany(_ => new[]
                    {
                        PlantEventType.ConnectionLost, PlantEventType.ConnectionRestored, PlantEventType.Gap
                    }), transitions.Select(value => value.Type));
                    Assert.All(transitions.Where(value => value.Type == PlantEventType.Gap), value =>
                    {
                        Assert.NotNull(value.GapStart);
                        Assert.True(value.GapEnd > value.GapStart);
                    });
                    if (capacityPause) Assert.All(transitions, value => Assert.Equal("buffer-capacity", value.Reason));
                }
                Assert.False(running.IsCompleted);
            }

            logger.Clear();
            await container.StopAsync(ct);
            await logger.WaitForAsync("OPC UA session interrupted", running, ct);
            await sourceCancellation.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(running.IsCompletedSuccessfully);
        }
        finally
        {
            await sourceCancellation.CancelAsync();
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException) when (sourceCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task ObserveAllTagsAsync(ChannelReader<Sample> reader, Task running,
        HashSet<string> expected, DateTimeOffset after, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        while (!expected.IsSubsetOf(seen))
        {
            var next = reader.ReadAsync(ct).AsTask();
            if (await Task.WhenAny(running, next) == running)
            {
                await running;
                throw new InvalidOperationException("The source stopped before all tags resumed.");
            }

            Sample sample;
            try { sample = await next; }
            catch (OperationCanceledException exception) when (ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Missing fresh tags after recovery: {string.Join(", ", expected.Except(seen))}", exception);
            }
            if (sample.SourceTimestamp >= after)
            {
                Assert.Equal(TimeSpan.Zero, sample.SourceTimestamp.Offset);
                seen.Add(sample.Tag);
            }
        }
    }

    private sealed class RecoveryLog(ITestOutputHelper output) : ILogger<OpcUaSourceAdapter>, ILoggerProvider
    {
        private readonly Channel<string> entries = Channel.CreateUnbounded<string>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.StartsWith("OPC UA session ", StringComparison.Ordinal)
                || message.StartsWith("OPC UA subscription ", StringComparison.Ordinal))
            {
                entries.Writer.TryWrite(message);
                output.WriteLine(message);
            }
            else if (logLevel >= LogLevel.Warning)
            {
                output.WriteLine(message);
            }
        }

        public void Clear()
        {
            while (entries.Reader.TryRead(out _)) { }
        }

        public async Task WaitForAsync(string message, Task running, CancellationToken ct)
        {
            while (true)
            {
                var next = entries.Reader.ReadAsync(ct).AsTask();
                if (await Task.WhenAny(running, next) == running)
                {
                    await running;
                    throw new InvalidOperationException("The source stopped before a recovery log arrived.");
                }

                if ((await next).StartsWith(message, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
    }

    private sealed class CapturingEvents : IPlantEventPublisher
    {
        public ConcurrentQueue<PlantEvent> Values { get; } = new();
        public Task PublishAsync(PlantEvent plantEvent, CancellationToken cancellationToken)
        {
            Values.Enqueue(plantEvent);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
