using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources.OpcUa;

namespace Telemetrix.Edge.Bridge.Sources.Mqtt;

public sealed class MqttSourceAdapter(IOptions<MqttOptions> options, TimeProvider timeProvider,
    ILogger<MqttSourceAdapter> logger, IPlantEventPublisher eventPublisher,
    SqliteBuffer? buffer = null) : ISourceAdapter
{
    public async Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var trackers = new Dictionary<(string Plant, string Line), PlantEventTracker>();
        using var admission = new SemaphoreSlim(1, 1);
        using var decoder = new MqttSampleDecoder(timeProvider, logger);
        var clearSession = buffer?.IsCollectionPaused == true;
        var attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (buffer?.IsCollectionPaused == true)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken);
                using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var sessionToken = sessionStop.Token;
                using var client = new MqttClientFactory().CreateMqttClient();
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.DisconnectedAsync += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
                client.ApplicationMessageReceivedAsync += async args =>
                {
                    // Do not ACK cancelled admission; normal persistent-session reconnect can redeliver it.
                    args.AutoAcknowledge = false;
                    try
                    {
                        await ready.Task.WaitAsync(sessionToken);
                        await admission.WaitAsync(sessionToken);
                        try
                        {
                            // A persistent session can retain an older, wider subscription after config changes.
                            if (MqttTopicFilterComparer.Compare(args.ApplicationMessage.Topic, settings.TopicFilter)
                                != MqttTopicFilterCompareResult.IsMatch)
                            {
                                await args.AcknowledgeAsync(sessionToken);
                                return;
                            }
                            var sample = decoder.Decode(args.ApplicationMessage.Topic, args.ApplicationMessage.Payload.ToArray());
                            if (sample is null)
                            {
                                await args.AcknowledgeAsync(sessionToken);
                                return;
                            }
                            var key = (sample.Plant, sample.Line);
                            if (!trackers.TryGetValue(key, out var tracker))
                            {
                                tracker = new PlantEventTracker(Options.Create(new OpcUaOptions
                                {
                                    Plant = sample.Plant, Line = sample.Line, Endpoint = settings.Endpoint
                                }), eventPublisher, timeProvider);
                                trackers.Add(key, tracker);
                                await tracker.RestoredAsync(sessionToken);
                            }
                            tracker.TrackAssets([sample.Asset]);
                            await tracker.SampleAsync(sample, sessionToken);
                            await writer.WriteAsync(sample, sessionToken);
                            await args.AcknowledgeAsync(sessionToken);
                        }
                        finally { admission.Release(); }
                    }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                    catch (MqttCommunicationException) { disconnected.TrySetResult(); }
                    catch (Exception exception) { failure.TrySetException(exception); }
                };
                var capacity = WaitForCapacityAsync(sessionToken);
                var capacityPause = false;
                try
                {
                    if (clearSession)
                    {
                        // Capacity pauses intentionally do not collect broker history (ADR-0013).
                        using var reset = new MqttClientFactory().CreateMqttClient();
                        await reset.ConnectAsync(Connection(settings, true), cancellationToken);
                        await reset.DisconnectAsync(cancellationToken: cancellationToken);
                        clearSession = false;
                    }
                    await client.ConnectAsync(Connection(settings, false), sessionToken);
                    await admission.WaitAsync(sessionToken);
                    try
                    {
                        foreach (var tracker in trackers.Values) await tracker.RestoredAsync(sessionToken);
                    }
                    finally { admission.Release(); }
                    ready.TrySetResult();
                    var subscribed = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(filter => filter.WithTopic(settings.TopicFilter).WithAtLeastOnceQoS()).Build(), sessionToken);
                    if (subscribed.Items.Any(item => (int)item.ResultCode >= 128))
                        throw new InvalidOperationException("The MQTT broker rejected the topic subscription.");
                    logger.LogInformation("MQTT subscription created at {Endpoint} with {TopicFilter}", settings.Endpoint, settings.TopicFilter);
                    attempt = 0;
                    var completed = await Task.WhenAny(disconnected.Task, capacity, failure.Task);
                    await completed;
                    capacityPause = completed == capacity;
                }
                catch (MqttCommunicationException exception)
                {
                    logger.LogWarning(exception, "MQTT broker unavailable; retrying connection");
                }
                finally
                {
                    await sessionStop.CancelAsync();
                    client.Dispose();
                    try { await capacity; }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                }
                cancellationToken.ThrowIfCancellationRequested();
                await admission.WaitAsync(cancellationToken);
                try
                {
                    foreach (var tracker in trackers.Values)
                        await tracker.LostAsync(capacityPause ? "buffer-capacity" : "source-unavailable", cancellationToken);
                }
                finally { admission.Release(); }
                clearSession |= capacityPause;
                if (!capacityPause)
                {
                    await Task.Delay(ReconnectBackoff.Delay(attempt, Random.Shared.NextDouble()), timeProvider, cancellationToken);
                    attempt = Math.Min(attempt + 1, 4);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            // Cancellation stops new admissions; let an in-flight callback release its tracker first.
            await admission.WaitAsync(CancellationToken.None);
            try { foreach (var tracker in trackers.Values) tracker.Dispose(); }
            finally { admission.Release(); }
        }
    }

    private static MqttClientOptions Connection(MqttOptions settings, bool clean) => new MqttClientOptionsBuilder()
        .WithTcpServer(settings.Host, settings.Port).WithClientId(settings.ClientId)
        .WithProtocolVersion(MqttProtocolVersion.V311).WithCleanSession(clean)
        .WithKeepAlivePeriod(TimeSpan.FromSeconds(5)).WithTimeout(TimeSpan.FromSeconds(5)).Build();

    private async Task WaitForCapacityAsync(CancellationToken ct)
    {
        while (buffer?.IsCollectionPaused != true)
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, ct);
        logger.LogWarning("MQTT collection stopping for buffer-capacity; replay remains active");
    }
}
