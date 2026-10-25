namespace Telemetrix.Edge.Bridge.Sources.Mqtt;

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicFilter { get; set; } = "plant/+/+/+/+";
    public string ClientId { get; set; } = "telemetrix-bridge";
    public string Endpoint => $"mqtt://{Host}:{Port}";
}
