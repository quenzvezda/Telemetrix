namespace Telemetrix.Contracts;

public sealed class TelemetryValidationException(string reason, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Reason { get; } = reason;
}
