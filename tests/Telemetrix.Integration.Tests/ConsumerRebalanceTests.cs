using Microsoft.Extensions.DependencyInjection;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Integration.Tests;

public class ConsumerRebalanceTests
{
    [Fact]
    public async Task JoiningConsumerDuringAnInflightWritePreservesAllMeasurements()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = environment.Host(services =>
        {
            services.AddSingleton<MeasurementWriter>();
            services.AddSingleton<IMeasurementWriter>(provider => new BlockingWriter(
                provider.GetRequiredService<MeasurementWriter>(), entered, release));
        });
        using var second = environment.Host();
        try
        {
            for (var i = 0; i < 6; i++) await environment.ProduceAsync(DeliveryTestEnvironment.Batch(i), i, ct);
            await first.StartAsync(ct);
            await entered.Task.WaitAsync(ct);
            await second.StartAsync(ct);
            release.TrySetResult();
            await environment.WaitForMembersAsync(2, ct);
            for (var i = 6; i < 60; i++) await environment.ProduceAsync(DeliveryTestEnvironment.Batch(i), i % 6, ct);

            await environment.WaitForOffsetsAsync([10, 10, 10, 10, 10, 10], ct);
            Assert.Equal(600, await environment.CountAsync(ct));
            await first.StopAsync(ct);
            await environment.WaitForMembersAsync(1, ct);
            Assert.Equal(600, await environment.CountAsync(ct));
        }
        finally
        {
            release.TrySetResult();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Task.WhenAll(first.StopAsync(cleanup.Token), second.StopAsync(cleanup.Token));
        }
    }

    private sealed class BlockingWriter(IMeasurementWriter inner, TaskCompletionSource entered,
        TaskCompletionSource release) : IMeasurementWriter
    {
        public async Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return await inner.WriteAsync(batchId, samples, cancellationToken);
        }
    }
}
