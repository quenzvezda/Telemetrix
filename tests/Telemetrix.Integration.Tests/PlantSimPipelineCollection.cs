namespace Telemetrix.Integration.Tests;

// The production Docker build and three-container acceptance check must not
// contend with the broker/OPC restart deadlines in the other integration classes.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlantSimPipelineCollection
{
    public const string Name = "PlantSim production pipeline";
}
