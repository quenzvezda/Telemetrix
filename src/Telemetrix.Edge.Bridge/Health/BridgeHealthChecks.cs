using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Telemetrix.Edge.Bridge.Buffer;
using Telemetrix.Edge.Bridge.Publishing;
using Telemetrix.Edge.Bridge.Sources;

namespace Telemetrix.Edge.Bridge.Health;

public sealed class SourceHealthCheck(ISourceAdapter source) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(source.IsConnected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Source is not connected."));
}

public sealed class BufferHealthCheck(SqliteBuffer buffer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(buffer.Depth.Batches == 0 ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Accepted batches are waiting for delivery."));
}

public sealed class KafkaHealthCheck(IOptions<KafkaOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                using var admin = new AdminClientBuilder(new AdminClientConfig
                    { BootstrapServers = options.Value.BootstrapServers, SocketTimeoutMs = 2000 }).Build();
                return admin.GetMetadata(TimeSpan.FromSeconds(2)).Brokers.Count > 0
                    ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Kafka has no reachable brokers.");
            }
            catch (KafkaException) { return HealthCheckResult.Unhealthy("Kafka metadata is unavailable."); }
        }, cancellationToken);
}
