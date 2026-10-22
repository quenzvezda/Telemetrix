using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer.Tests;

public class TelemetryBatchHandlerTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MixedBatchStoresValidatedSamplesAndPublishesOnlyUnknownTags()
    {
        using var resolver = new TagResolver(new Catalog(), Options.Create(new TagResolutionOptions()));
        var writer = new Writer();
        var publisher = new Publisher();
        var handler = new TelemetryBatchHandler(resolver, writer, publisher, NullLogger<TelemetryBatchHandler>.Instance,
            ResiliencePipeline.Empty);
        var batch = Batch();

        await handler.HandleAsync(new RawBatchDelivery(batch, "Pump01", 2, 42), TestContext.Current.CancellationToken);

        Assert.Equal(batch.BatchId, writer.BatchId);
        Assert.Equal(new[] { Quality.Good, Quality.Bad, Quality.Uncertain, Quality.Bad },
            writer.Samples.Select(sample => sample.Quality));
        Assert.Equal(new double?[] { 52.8, 500, 25, null }, writer.Samples.Select(sample => sample.Value));
        Assert.All(writer.Samples, sample => Assert.Equal(Timestamp, sample.SourceTimestamp));
        Assert.Equal(new[] { 1, 2, 1, 1 }, writer.Samples.Select(sample => sample.TagId));
        var unknown = Assert.IsType<TelemetryBatch>(publisher.Batch);
        Assert.Equal(batch.BatchId, unknown.BatchId);
        Assert.Equal(batch.Samples[^1], Assert.Single(unknown.Samples));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StorageAndDeadLetterFailuresPropagateToTheConsumer(bool failWrite)
    {
        using var resolver = new TagResolver(new Catalog(), Options.Create(new TagResolutionOptions()));
        var failure = new InvalidOperationException("test failure");
        var writer = new Writer { Failure = failWrite ? failure : null };
        var publisher = new Publisher { Failure = failWrite ? null : failure };
        var handler = new TelemetryBatchHandler(resolver, writer, publisher, NullLogger<TelemetryBatchHandler>.Instance,
            ResiliencePipeline.Empty);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new RawBatchDelivery(Batch(), "Pump01", 2, 42), TestContext.Current.CancellationToken)));
        if (failWrite) Assert.Null(publisher.Batch);
        else
        {
            Assert.Equal(4, writer.Samples.Count);
            Assert.NotNull(publisher.Batch);
        }
    }

    private static TelemetryBatch Batch() => new(1, Guid.NewGuid(), "opcua", "opc.tcp://test:50000",
        "Plant01", "Line01", "Pump01", Timestamp,
        [new("FlowRate", 52.8, Timestamp, Timestamp.AddHours(1), 0),
         new("MotorTemperature", 500, Timestamp, Timestamp, 0),
         new("FlowRate", 25, Timestamp, Timestamp, 0x40000000),
         new("FlowRate", null, Timestamp, Timestamp, 0),
         new("Torque", 42, Timestamp, Timestamp, 0)]);

    private sealed class Catalog : ITagCatalog
    {
        public Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagMetadata>>([new(1, "Pump01", "FlowRate", "m3/h", 0, 80),
                new(2, "Pump01", "MotorTemperature", "C", -20, 120)]);
        public Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken) =>
            Task.FromResult<TagMetadata?>(null);
        public Task<TagMetadata?> CreateAsync(string assetId, string tag, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("default policy must not create tags");
    }

    private sealed class Writer : IMeasurementWriter
    {
        public Guid BatchId { get; private set; }
        public IReadOnlyList<ValidatedSample> Samples { get; private set; } = [];
        public Exception? Failure { get; init; }
        public Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken)
        {
            BatchId = batchId;
            Samples = samples;
            return Failure is { } failure ? Task.FromException<int>(failure) : Task.FromResult(samples.Count);
        }
    }

    private sealed class Publisher : IDeadLetterPublisher
    {
        public TelemetryBatch? Batch { get; private set; }
        public Exception? Failure { get; init; }
        public Task PublishUnknownTagsAsync(TelemetryBatch batch, CancellationToken cancellationToken)
        {
            Batch = batch;
            return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }
    }
}
