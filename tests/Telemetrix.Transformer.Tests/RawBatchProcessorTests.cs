using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer.Tests;

public class RawBatchProcessorTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 22, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FailedStorageCannotCommit()
    {
        using var resolver = new TagResolver(new Catalog(), Options.Create(new TagResolutionOptions()));
        var failure = new InvalidOperationException("storage failed");
        var handler = new TelemetryBatchHandler(resolver, new Writer(() => throw failure), new Publisher(),
            NullLogger<TelemetryBatchHandler>.Instance);
        var processor = new RawBatchProcessor(handler);
        var committed = false;

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => processor.Process(
            Result(), () => committed = true, TestContext.Current.CancellationToken)));
        Assert.False(committed);
    }

    [Fact]
    public void CommitFollowsStorageAndUnknownTagAcknowledgementOnTheCallingThread()
    {
        using var resolver = new TagResolver(new Catalog(), Options.Create(new TagResolutionOptions()));
        var events = new List<string>();
        var handler = new TelemetryBatchHandler(resolver, new Writer(() => events.Add("write")),
            new Publisher(() => events.Add("dead-letter")), NullLogger<TelemetryBatchHandler>.Instance);
        var processor = new RawBatchProcessor(handler);
        var thread = Environment.CurrentManagedThreadId;

        processor.Process(Result(unknown: true), () =>
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            events.Add("commit");
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "write", "dead-letter", "commit" }, events);
    }

    private static ConsumeResult<string, byte[]> Result(bool unknown = false)
    {
        var samples = new List<BatchSample> { new("FlowRate", 20, Timestamp, Timestamp, 0) };
        if (unknown) samples.Add(new("Torque", 10, Timestamp, Timestamp, 0));
        var batch = new TelemetryBatch(1, Guid.NewGuid(), "opcua", "opc.tcp://test:50000",
            "Plant01", "Line01", "Pump01", Timestamp, samples);
        return new()
        {
            Topic = KafkaOptions.RawTopic, Partition = 0, Offset = 42,
            Message = new() { Key = batch.AssetId, Value = TelemetryJson.Serialize(batch) }
        };
    }

    private sealed class Catalog : ITagCatalog
    {
        public Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagMetadata>>([new(1, "Pump01", "FlowRate", "m3/h", 0, 80)]);
        public Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken) =>
            Task.FromResult<TagMetadata?>(null);
        public Task<TagMetadata?> CreateAsync(string assetId, string tag, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("must not create");
    }

    private sealed class Writer(Action write) : IMeasurementWriter
    {
        public Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken)
        {
            write();
            return Task.FromResult(samples.Count);
        }
    }

    private sealed class Publisher(Action? publish = null) : IDeadLetterPublisher
    {
        public Task PublishUnknownTagsAsync(TelemetryBatch batch, CancellationToken cancellationToken)
        {
            publish?.Invoke();
            return Task.CompletedTask;
        }
    }
}
