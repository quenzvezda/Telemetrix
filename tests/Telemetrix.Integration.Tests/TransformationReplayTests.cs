using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Scripting;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Integration.Tests;

public class TransformationReplayTests
{
    private const string ChangedScript = """
        def transform(batch, context):
            return {"samples": [{"tag": s["tag"] + "F", "value": s["value"] + 1000,
                                 "sourceTimestamp": s["sourceTimestamp"], "inputs": [s["index"]]}
                                for s in batch["samples"] if s["unit"] == "degC"]}
        """;

    // A crash after the database commit replays the Batch; the first durable result wins even after a script change.
    // A crash before the commit stores nothing, so the restarted version's result is the only one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CrashAndScriptChangeNeitherDuplicateNorMixDerivedResults(bool afterDatabaseCommit)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        await TransformationStorageTests.ApplyTransformationMigrationAsync(environment.Source, ct);
        using var scripts = new TransformationScripts();
        var original = TransformationPipelineTests.Version(scripts, "celsius_to_fahrenheit.py");
        var crashed = TransformationPipelineTests.Batch(0, "Tank01", "celsius-to-fahrenheit", ("Temperature", 25), ("Temperature", 30));
        var following = TransformationPipelineTests.Batch(1, "Tank01", "celsius-to-fahrenheit", ("Temperature", 35));
        using var first = environment.Host(services =>
        {
            services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
            services.AddSingleton<MeasurementWriter>();
            services.AddSingleton<IMeasurementWriter>(provider => new CrashWriter(
                provider.GetRequiredService<MeasurementWriter>(), afterDatabaseCommit));
        }, scripts.Settings());
        IHost? restarted = null;
        try
        {
            await environment.ProduceAsync(crashed, 0, ct);
            await environment.ProduceAsync(following, 0, ct);
            await first.StartAsync(ct);
            var consumer = Assert.Single(first.Services.GetServices<IHostedService>().OfType<RawBatchConsumer>());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ExecuteTask!.WaitAsync(ct));
            Assert.Equal(afterDatabaseCommit ? 4 : 0, await environment.CountAsync(ct));
            await first.StopAsync(ct);

            // Deployment with a changed script, then a duplicate delivery of the same Batch.
            scripts.Replace("celsius_to_fahrenheit.py", ChangedScript);
            var changed = TransformationPipelineTests.Version(scripts, "celsius_to_fahrenheit.py");
            Assert.NotEqual(original, changed);
            restarted = environment.Host(settings: scripts.Settings());
            await restarted.StartAsync(ct);
            await environment.WaitForOffsetsAsync([2], ct);
            await environment.ProduceAsync(crashed, 0, ct);
            await environment.WaitForOffsetsAsync([3], ct);

            await using var connection = await environment.Source.OpenConnectionAsync(ct);
            Assert.Equal(6, await environment.CountAsync(ct));
            var runs = (await connection.QueryAsync<(Guid BatchId, string Version, int Outputs)>(new CommandDefinition(
                "SELECT batch_id, script_version, output_count FROM transformation_run", cancellationToken: ct))).ToDictionary(run => run.BatchId);
            Assert.Equal(2, runs.Count);
            Assert.Equal(afterDatabaseCommit ? original : changed, runs[crashed.BatchId].Version);
            Assert.Equal(changed, runs[following.BatchId].Version);
            // Derived rows keep source timestamps and per-Asset order; one version per Batch.
            var derived = (await connection.QueryAsync<(DateTime Time, double Value)>(new CommandDefinition("""
                SELECT m.time, m.value FROM measurement m JOIN tag t USING (tag_id)
                WHERE t.name = 'TemperatureF' ORDER BY m.ingested_at, m.time
                """, cancellationToken: ct))).ToArray();
            Assert.Equal([
                (TransformationPipelineTests.Time(0, 0), afterDatabaseCommit ? 77d : 1025d),
                (TransformationPipelineTests.Time(0, 1), afterDatabaseCommit ? 86d : 1030d),
                (TransformationPipelineTests.Time(1, 0), 1035d)], derived);
            Assert.Equal(25d, await connection.ExecuteScalarAsync<double>(new CommandDefinition(
                "SELECT value FROM measurement m JOIN tag t USING (tag_id) WHERE t.name = 'Temperature' ORDER BY m.time LIMIT 1",
                cancellationToken: ct)));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await first.StopAsync(cleanup.Token);
            if (restarted is not null)
            {
                await restarted.StopAsync(cleanup.Token);
                restarted.Dispose();
            }
        }
    }

    private sealed class CrashWriter(IMeasurementWriter inner, bool afterDatabaseCommit) : IMeasurementWriter
    {
        public Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken) =>
            WriteAsync(batchId, samples, null, cancellationToken);

        public async Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, TransformationRecord? transformation,
            CancellationToken cancellationToken)
        {
            if (afterDatabaseCommit) await inner.WriteAsync(batchId, samples, transformation, cancellationToken);
            throw new OperationCanceledException("Simulated process crash before Kafka offset commit.");
        }
    }
}
