using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Integration.Tests;

[CollectionDefinition("Observability integration", DisableParallelization = true)]
public class ObservabilityIntegrationCollection;

[Collection("Observability integration")]
public class TransformerMetricsTests
{
    [Fact]
    public async Task LagGrowsDuringPausedConsumptionAndCountersIgnoreReplayDuplicates()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        await using var environment = new DeliveryTestEnvironment();
        await environment.StartAsync(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long written = 0, deadLettered = 0, durations = 0;
        var lag = new ConcurrentDictionary<int, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, target) =>
        {
            if (instrument.Meter.Name == TransformerMetrics.MeterName) target.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "telemetrix.rows.written") Interlocked.Add(ref written, value);
            if (instrument.Name == "telemetrix.batches.deadlettered") Interlocked.Add(ref deadLettered, value);
            if (instrument.Name == "telemetrix.consumer.lag")
                foreach (var tag in tags) if (tag.Key == "partition") lag[(int)tag.Value!] = value;
        });
        listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            Assert.True(value >= 0);
            Interlocked.Increment(ref durations);
        });
        listener.Start();
        using var host = environment.Host(services =>
        {
            services.AddSingleton<MeasurementWriter>();
            services.AddSingleton<IMeasurementWriter>(provider => new BlockingWriter(
                provider.GetRequiredService<MeasurementWriter>(), entered, release));
        });
        try
        {
            await host.StartAsync(ct);
            for (var i = 0; i < 3; i++) await environment.ProduceAsync(DeliveryTestEnvironment.Batch(i), 0, ct);
            await entered.Task.WaitAsync(ct);
            while (!lag.Values.Any(value => value >= 3))
            {
                listener.RecordObservableInstruments();
                await Task.Delay(100, ct);
            }
            Assert.Equal(0, Interlocked.Read(ref written));
            release.TrySetResult();
            await environment.WaitForCountAsync(30, ct);
            while (lag.Values.Sum() != 0)
            {
                listener.RecordObservableInstruments();
                await Task.Delay(100, ct);
            }
            Assert.Equal(30, Interlocked.Read(ref written));
            await environment.ProduceAsync(DeliveryTestEnvironment.Batch(0), 0, ct);
            while (Interlocked.Read(ref durations) < 4) await Task.Delay(50, ct);
            Assert.Equal(30, Interlocked.Read(ref written));
            await environment.Producer.ProduceAsync(Telemetrix.Transformer.Consuming.KafkaOptions.RawTopic,
                new Message<string, byte[]> { Key = "Pump01", Value = Encoding.UTF8.GetBytes("invalid-json") }, ct);
            while (Interlocked.Read(ref deadLettered) == 0) await Task.Delay(50, ct);
            Assert.Equal(1, Interlocked.Read(ref deadLettered));
        }
        finally
        {
            release.TrySetResult();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StopAsync(cleanup.Token);
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
