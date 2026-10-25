using System.Threading.Channels;
using Telemetrix.Contracts;

namespace Telemetrix.Edge.Bridge.Sources;

public interface ISourceAdapter
{
    bool IsConnected => false;
    Task RunAsync(ChannelWriter<Sample> writer, CancellationToken cancellationToken);
}
