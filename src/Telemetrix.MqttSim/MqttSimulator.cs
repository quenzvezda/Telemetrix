using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Exceptions;
using Telemetrix.Contracts;
using Telemetrix.Simulation;

namespace Telemetrix.MqttSim;

public static class MqttSimulatorServices
{
    public static IServiceCollection AddMqttSimulator(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MqttSimOptions>().Bind(configuration.GetSection("MqttSim"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host) && o.Port is > 0 and <= 65535,
                "MqttSim requires a host and valid port.")
            .Validate(o => new[] { o.Plant, o.Line, o.ClientId }.All(s =>
                !string.IsNullOrWhiteSpace(s) && s.IndexOfAny(['/', '+', '#', '\0']) < 0),
                "MqttSim identity must contain nonempty topic segments and a client id.")
            .Validate(o => o.BadQualityTag is null || PlantTags.All.Any(t => t.Key == o.BadQualityTag),
                "MqttSim:BadQualityTag must name a known Asset.Tag.")
            .Validate(o => o.LineCount is >= 1 and <= 1000 && o.MaxInFlight is >= 1 and <= 256,
                "MqttSim requires 1-1000 lines and 1-256 in-flight publications.")
            .Validate(o => o.TickInterval >= TimeSpan.FromMilliseconds(10) && o.TickInterval <= TimeSpan.FromMinutes(1),
                "MqttSim:TickInterval must be between 10 milliseconds and one minute.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PumpingLine>();
        services.AddSingleton<MqttSimulator>();
        services.AddHostedService(provider => provider.GetRequiredService<MqttSimulator>());
        return services;
    }
}

public sealed class MqttSimulator(IOptions<MqttSimOptions> options, PumpingLine model,
    TimeProvider timeProvider, ILogger<MqttSimulator> logger) : BackgroundService
{
    private int running;
    private int completing;
    private long published;
    public bool IsReady => Volatile.Read(ref running) != 0;
    public long PublishedSamples => Interlocked.Read(ref published);

    public Task CompleteAsync(CancellationToken ct)
    {
        Volatile.Write(ref completing, 1);
        return ExecuteTask?.WaitAsync(ct) ?? Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var client = new MqttClientFactory().CreateMqttClient();
        client.DisconnectedAsync += _ => { Volatile.Write(ref running, 0); return Task.CompletedTask; };
        var connection = new MqttClientOptionsBuilder().WithTcpServer(settings.Host, settings.Port)
            .WithClientId(settings.ClientId).WithProtocolVersion(MqttProtocolVersion.V311).Build();
        var lines = Enumerable.Range(0, settings.LineCount).Select(index => index == 0 ? model : new PumpingLine(timeProvider)).ToArray();
        var random = Enumerable.Range(0, settings.LineCount).Select(index => new Random(settings.Seed + index)).ToArray();
        using var connecting = new SemaphoreSlim(1, 1);
        using var timer = new PeriodicTimer(settings.TickInterval, timeProvider);
        try
        {
            do
            {
                if (Volatile.Read(ref completing) != 0) break;
                var timestamp = timeProvider.GetUtcNow();
                var messages = new List<MqttApplicationMessage>(settings.LineCount * PlantTags.All.Count);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (settings.BadQualityTag is not null)
                        lines[index].SetBadQuality(settings.BadQualityTag, TimeSpan.FromSeconds(2));
                    foreach (var value in lines[index].Step(settings.TickInterval, random[index]))
                    {
                        var scalar = value.Value is bool boolean ? (boolean ? "true" : "false")
                            : Convert.ToDouble(value.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                        using var json = JsonDocument.Parse(scalar);
                        var payload = new MqttTagPayload(json.RootElement.Clone(), timestamp, value.IsBad ? MqttQuality.Bad : MqttQuality.Good);
                        var line = index == 0 ? settings.Line : $"{settings.Line}_{index + 1:D4}";
                        var asset = index == 0 ? value.Tag.Asset : $"{value.Tag.Asset}_{index + 1:D4}";
                        messages.Add(new MqttApplicationMessageBuilder()
                            .WithTopic($"plant/{settings.Plant}/{line}/{asset}/{value.Tag.Name}")
                            .WithPayload(TelemetryJson.Serialize(payload)).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainFlag().Build());
                    }
                }
                // Complete a source tick before starting the next so each tag's
                // timestamps stay ordered, even with bounded parallel publication.
                await Parallel.ForEachAsync(messages, new ParallelOptions
                {
                    MaxDegreeOfParallelism = settings.MaxInFlight, CancellationToken = stoppingToken
                }, async (message, token) => await PublishAsync(client, connection, message, connecting, token));
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task PublishAsync(IMqttClient client, MqttClientOptions connection,
        MqttApplicationMessage message, SemaphoreSlim connecting, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                if (!client.IsConnected)
                {
                    await connecting.WaitAsync(ct);
                    try
                    {
                        if (!client.IsConnected)
                        {
                            await client.ConnectAsync(connection, ct);
                            logger.LogInformation("MqttSim connected to {Host}:{Port}", options.Value.Host, options.Value.Port);
                        }
                    }
                    finally { connecting.Release(); }
                }
                var result = await client.PublishAsync(message, ct);
                if ((int)result.ReasonCode >= 128)
                    throw new InvalidOperationException($"MQTT publish rejected: {result.ReasonCode}");
                Volatile.Write(ref running, client.IsConnected ? 1 : 0);
                Interlocked.Increment(ref published);
                return;
            }
            catch (MqttCommunicationException exception)
            {
                Volatile.Write(ref running, 0);
                logger.LogWarning(exception, "MqttSim broker unavailable; retrying the same sample");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Min(attempt++, 4)))), timeProvider, ct);
            }
        }
    }
}
