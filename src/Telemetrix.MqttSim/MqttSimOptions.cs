namespace Telemetrix.MqttSim;

public sealed class MqttSimOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "telemetrix-mqttsim";
    public string Plant { get; set; } = "Plant01";
    public string Line { get; set; } = "Line01";
    public int Seed { get; set; } = 1729;
    public string? BadQualityTag { get; set; }
}
