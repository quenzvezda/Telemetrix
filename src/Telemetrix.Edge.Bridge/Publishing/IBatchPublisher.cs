using System.Threading.Channels;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Publishing;

public interface IBatchPublisher
{
    Task RunAsync(ChannelReader<TelemetryBatch> batches, CancellationToken cancellationToken);
}
