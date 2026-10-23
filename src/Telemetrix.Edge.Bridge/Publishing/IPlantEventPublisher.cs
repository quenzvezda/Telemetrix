using Telemetrix.Contracts;
using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Edge.Bridge.Publishing;

public interface IPlantEventPublisher
{
    Task PublishAsync(PlantEvent plantEvent, CancellationToken cancellationToken);
}

public sealed class PlantEventPublisher(StoreAndForwardQueue queue) : IPlantEventPublisher
{
    public Task PublishAsync(PlantEvent plantEvent, CancellationToken cancellationToken) =>
        queue.PublishAsync(new BufferedEntry(KafkaOptions.EventsTopic, plantEvent.AssetId,
            TelemetryJson.Serialize(plantEvent), 0, plantEvent.OccurredAt), cancellationToken);
}
