using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Integration.Tests;

public class KillMidBatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashBeforeOffsetCommitReplaysTheBatchWithoutGapsOrDuplicates(bool afterDatabaseCommit)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        var replayed = new ConcurrentDictionary<Guid, int>();
        using var first = environment.Host(services =>
        {
            services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
            services.AddSingleton<MeasurementWriter>();
            services.AddSingleton<IMeasurementWriter>(provider => new CrashWriter(
                provider.GetRequiredService<MeasurementWriter>(), afterDatabaseCommit));
        });
        using var restarted = environment.Host(services =>
        {
            services.AddSingleton<MeasurementWriter>();
            services.AddSingleton<IMeasurementWriter>(provider => new ReplayWriter(
                provider.GetRequiredService<MeasurementWriter>(), replayed));
        });
        var batch = DeliveryTestEnvironment.Batch(0);
        var following = DeliveryTestEnvironment.Batch(1);
        try
        {
            await environment.ProduceAsync(batch, 0, ct);
            await environment.ProduceAsync(following, 0, ct);
            await first.StartAsync(ct);
            var consumer = Assert.Single(first.Services.GetServices<IHostedService>().OfType<RawBatchConsumer>());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ExecuteTask!.WaitAsync(ct));
            Assert.Equal(afterDatabaseCommit ? 10 : 0, await environment.CountAsync(ct));
            using (var inspector = new ConsumerBuilder<string, byte[]>(RawBatchConsumer.CreateConfig(
                new KafkaOptions { BootstrapServers = environment.Kafka.GetBootstrapAddress() })).Build())
            {
                var offset = Assert.Single(inspector.Committed(
                    [new TopicPartition(KafkaOptions.RawTopic, new Partition(0))], TimeSpan.FromSeconds(10)));
                Assert.Equal(Offset.Unset, offset.Offset);
            }

            await first.StopAsync(ct);
            await restarted.StartAsync(ct);
            await environment.WaitForOffsetsAsync([2], ct);

            Assert.Equal(20, await environment.CountAsync(ct));
            Assert.Equal(afterDatabaseCommit ? 0 : 10, replayed[batch.BatchId]);
            Assert.Equal(10, replayed[following.BatchId]);
            Assert.Equal(2, replayed.Count);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Task.WhenAll(first.StopAsync(cleanup.Token), restarted.StopAsync(cleanup.Token));
        }
    }

    private sealed class CrashWriter(IMeasurementWriter inner, bool afterDatabaseCommit) : IMeasurementWriter
    {
        public async Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken)
        {
            if (afterDatabaseCommit) await inner.WriteAsync(batchId, samples, cancellationToken);
            throw new OperationCanceledException("Simulated process crash before Kafka offset commit.");
        }
    }

    private sealed class ReplayWriter(IMeasurementWriter inner, ConcurrentDictionary<Guid, int> replayed) : IMeasurementWriter
    {
        public async Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken)
        {
            var inserted = await inner.WriteAsync(batchId, samples, cancellationToken);
            replayed[batchId] = inserted;
            return inserted;
        }
    }
}
