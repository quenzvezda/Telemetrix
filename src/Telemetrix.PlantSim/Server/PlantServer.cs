using Opc.Ua;
using Opc.Ua.Server;
using Telemetrix.PlantSim.Model;

namespace Telemetrix.PlantSim.Server;

public sealed class PlantServer(ITelemetryContext telemetry, TimeProvider timeProvider, PumpingLine line, int seed)
    : StandardServer(telemetry, timeProvider)
{
    private readonly TimeProvider clock = timeProvider;
    public PlantNodeManager? PlantNodes { get; private set; }

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server,
        ApplicationConfiguration configuration)
    {
        PlantNodes = new PlantNodeManager(server, configuration, line, clock, seed);
        return new MasterNodeManager(server, configuration, null, new INodeManager[] { PlantNodes });
    }
}
