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
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PumpingLine>();
        services.AddHostedService<MqttSimulator>();
        return services;
    }
}

public sealed class MqttSimulator(IOptions<MqttSimOptions> options, PumpingLine model,
    TimeProvider timeProvider, ILogger<MqttSimulator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var client = new MqttClientFactory().CreateMqttClient();
        var connection = new MqttClientOptionsBuilder().WithTcpServer(settings.Host, settings.Port)
            .WithClientId(settings.ClientId).WithProtocolVersion(MqttProtocolVersion.V311).Build();
        var rng = new Random(settings.Seed);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        try
        {
            do
            {
                var timestamp = timeProvider.GetUtcNow();
                if (settings.BadQualityTag is not null)
                    model.SetBadQuality(settings.BadQualityTag, TimeSpan.FromSeconds(2));
                foreach (var value in model.Step(TimeSpan.FromSeconds(1), rng))
                {
                    var scalar = value.Value is bool boolean ? (boolean ? "true" : "false")
                        : Convert.ToDouble(value.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                    using var json = JsonDocument.Parse(scalar);
                    var payload = new MqttTagPayload(json.RootElement.Clone(), timestamp,
                        value.IsBad ? MqttQuality.Bad : MqttQuality.Good);
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic($"plant/{settings.Plant}/{settings.Line}/{value.Tag.Asset}/{value.Tag.Name}")
                        .WithPayload(TelemetryJson.Serialize(payload)).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag().Build();
                    var attempt = 0;
                    while (true)
                    {
                        try
                        {
                            if (!client.IsConnected)
                            {
                                await client.ConnectAsync(connection, stoppingToken);
                                logger.LogInformation("MqttSim connected to {Host}:{Port}", settings.Host, settings.Port);
                            }
                            var result = await client.PublishAsync(message, stoppingToken);
                            if ((int)result.ReasonCode >= 128)
                                throw new InvalidOperationException($"MQTT publish rejected: {result.ReasonCode}");
                            break;
                        }
                        catch (MqttCommunicationException exception)
                        {
                            logger.LogWarning(exception, "MqttSim broker unavailable; retrying the same sample");
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Min(attempt++, 4)))),
                                timeProvider, stoppingToken);
                        }
                    }
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
