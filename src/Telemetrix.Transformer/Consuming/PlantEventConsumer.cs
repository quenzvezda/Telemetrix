using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Polly;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer.Consuming;

public sealed class PlantEventConsumer(IOptions<KafkaOptions> options, PlantEventWriter writer,
    ResiliencePipeline storageRetry, ILogger<PlantEventConsumer> logger) : BackgroundService
{
    public const string Topic = "plant.events";
    public const string Group = "transformer-events";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Factory.StartNew(
        () => Consume(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Consume(CancellationToken ct)
    {
        var config = RawBatchConsumer.CreateConfig(options.Value);
        config.GroupId = Group;
        config.ClientId = "telemetrix-transformer-events";
        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        try
        {
            consumer.Subscribe(Topic);
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]> delivery;
                try { delivery = consumer.Consume(ct); }
                catch (ConsumeException exception) when (exception.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    // Bridge owns topic creation; it may start after the Transformer.
                    logger.LogWarning("Waiting for the Bridge-owned plant event topic");
                    continue;
                }
                var plantEvent = TelemetryJson.DeserializePlantEvent(delivery.Message.Value);
                if (plantEvent.SchemaVersion != PlantEvent.CurrentSchemaVersion || plantEvent.EventId == Guid.Empty)
                    throw new InvalidOperationException("Unsupported or empty plant event identity; offset retained.");
                using var scope = logger.BeginScope(new Dictionary<string, object?>
                    { ["eventId"] = plantEvent.EventId, ["assetId"] = plantEvent.AssetId });
                storageRetry.ExecuteAsync(token => new ValueTask(writer.WriteAsync(plantEvent, token)), ct)
                    .GetAwaiter().GetResult();
                ct.ThrowIfCancellationRequested();
                consumer.Commit(delivery);
                logger.LogInformation("Stored and committed plant event {EventId} ({EventType})", plantEvent.EventId, plantEvent.Type);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { consumer.Close(); }
    }
}
