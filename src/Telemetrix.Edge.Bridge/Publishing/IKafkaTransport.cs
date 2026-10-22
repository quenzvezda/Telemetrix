using Telemetrix.Edge.Bridge.Buffer;

namespace Telemetrix.Edge.Bridge.Publishing;

public interface IKafkaTransport
{
    Task PublishAsync(BufferedEntry entry, CancellationToken cancellationToken);
}
