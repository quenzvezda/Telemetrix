namespace Telemetrix.PlantSim;

public sealed class PlantSimOptions
{
    public string Endpoint { get; set; } = "opc.tcp://localhost:4840";
    public string PkiPath { get; set; } = Path.Combine("pki", "plantsim");
    public string SecurityMode { get; set; } = "SignAndEncrypt";
    public int Seed { get; set; } = 42;
}
