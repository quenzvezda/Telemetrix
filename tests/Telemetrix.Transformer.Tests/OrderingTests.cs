using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Consuming;
using Telemetrix.Transformer.Pipeline;
using Telemetrix.Transformer.Storage;

namespace Telemetrix.Transformer.Tests;

public class OrderingTests
{
    [Fact]
    public void ConsecutiveBatchesPreserveInterleavedTagOrderThroughMappingStorageAndCommit()
    {
        var start = new DateTimeOffset(2026, 10, 22, 5, 0, 0, TimeSpan.Zero);
        var batches = Enumerable.Range(0, 2).Select(sequence => new TelemetryBatch(1, Guid.NewGuid(),
            "opcua", "opc.tcp://test:50000", "Plant01", "Line01", "Pump01", start,
            Enumerable.Range(sequence * 2, 2).SelectMany(i => new BatchSample[]
            {
                new("FlowRate", 20 + i, start.AddSeconds(i), start, 0),
                new("Running", 1, start.AddSeconds(i), start, 0)
            }).ToArray())).ToArray();
        using var resolver = new TagResolver(new Catalog(), Options.Create(new TagResolutionOptions()));
        var writer = new Writer();
        var publisher = new UnusedPublisher();
        var handler = new TelemetryBatchHandler(resolver, writer, publisher,
            NullLogger<TelemetryBatchHandler>.Instance, ResiliencePipeline.Empty);
        var processor = new RawBatchProcessor(handler, publisher);
        var committed = new List<Guid>();

        foreach (var batch in batches)
            processor.Process(new ConsumeResult<string, byte[]>
            {
                Topic = KafkaOptions.RawTopic, Partition = 2, Offset = committed.Count,
                Message = new() { Key = batch.AssetId, Value = TelemetryJson.Serialize(batch) }
            }, () =>
            {
                Assert.Equal(batch.BatchId, writer.Batches[^1]);
                Assert.Equal((committed.Count + 1) * 4, writer.Samples.Count);
                committed.Add(batch.BatchId);
            }, TestContext.Current.CancellationToken);

        Assert.Equal(batches.Select(b => b.BatchId), committed);
        Assert.Equal(batches.SelectMany(b => b.Samples).Select(s => s.SourceTimestamp),
            writer.Samples.Select(s => s.SourceTimestamp));
        Assert.Equal(new[] { 1, 2, 1, 2, 1, 2, 1, 2 }, writer.Samples.Select(s => s.TagId));
        foreach (var tag in writer.Samples.GroupBy(s => s.TagId))
            Assert.Equal(tag.Select(s => s.SourceTimestamp).Order(), tag.Select(s => s.SourceTimestamp));
    }

    private sealed class Writer : IMeasurementWriter
    {
        public List<Guid> Batches { get; } = [];
        public List<ValidatedSample> Samples { get; } = [];
        public async Task<int> WriteAsync(Guid batchId, IReadOnlyList<ValidatedSample> samples, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Batches.Add(batchId);
            Samples.AddRange(samples);
            return samples.Count;
        }
    }

    private sealed class Catalog : ITagCatalog
    {
        public Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagMetadata>>([new(1, "Pump01", "FlowRate", "m3/h", 0, 80),
                new(2, "Pump01", "Running", null, null, null)]);
        public Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("all tags are known");
        public Task<TagMetadata?> CreateAsync(string assetId, string tag, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("all tags are known");
    }

    private sealed class UnusedPublisher : IDeadLetterPublisher, IInvalidBatchPublisher
    {
        public Task PublishUnknownTagsAsync(TelemetryBatch batch, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("all tags are known");
        public Task PublishAsync(ConsumeResult<string, byte[]> result, string reason, string error, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("valid batches must not be rejected");
    }
}
