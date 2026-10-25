using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Telemetrix.Transformer.Consuming;

namespace Telemetrix.Transformer.Health;

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

public sealed class DatabaseHealthCheck(NpgsqlDataSource source) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var command = source.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(deadline.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database is unavailable.");
        }
    }
}
