using Microsoft.Extensions.Options;
using Telemetrix.Contracts;
using Telemetrix.Transformer.Pipeline;

namespace Telemetrix.Transformer.Tests;

public class TagResolverTests
{
    [Fact]
    public async Task DefaultPolicySeparatesUnknownSamplesAndKeepsTheOriginalEnvelope()
    {
        var catalog = new MemoryCatalog();
        using var resolver = Resolver(catalog);
        await resolver.InitializeAsync(TestContext.Current.CancellationToken);
        var batch = Batch("FlowRate", "Torque", "FlowRate", "Torque");

        var result = await resolver.ResolveAsync(batch, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.KnownSamples.Count);
        Assert.All(result.KnownSamples, sample => Assert.Equal(1, sample.Tag.TagId));
        var rejected = Assert.IsType<TelemetryBatch>(result.DeadLetterBatch);
        Assert.Equal(batch.BatchId, rejected.BatchId);
        Assert.Equal(batch.SchemaVersion, rejected.SchemaVersion);
        Assert.Equal(batch.Source, rejected.Source);
        Assert.Equal(batch.Endpoint, rejected.Endpoint);
        Assert.Equal(batch.PlantId, rejected.PlantId);
        Assert.Equal(batch.LineId, rejected.LineId);
        Assert.Equal(batch.AssetId, rejected.AssetId);
        Assert.Equal(batch.CreatedAt, rejected.CreatedAt);
        Assert.Equal(new[] { batch.Samples[1], batch.Samples[3] }, rejected.Samples);
        Assert.Equal(1, catalog.Loads);
        Assert.Equal(1, catalog.Finds);
        Assert.Equal(0, catalog.Creates);
    }

    [Fact]
    public async Task CreatePolicyAddsMetadataOnceAndCachesIt()
    {
        var catalog = new MemoryCatalog();
        using var resolver = Resolver(catalog, TagResolutionOptions.Create);
        var batch = Batch("Torque", "Torque");

        var first = await resolver.ResolveAsync(batch, TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(batch, TestContext.Current.CancellationToken);

        Assert.Null(first.DeadLetterBatch);
        Assert.Null(second.DeadLetterBatch);
        Assert.Equal(2, first.KnownSamples.Count);
        var tag = first.KnownSamples[0].Tag;
        Assert.Equal("Pump01", tag.AssetId);
        Assert.Equal("Torque", tag.Name);
        Assert.Null(tag.Unit);
        Assert.Null(tag.MinValid);
        Assert.Null(tag.MaxValid);
        Assert.Equal(tag, second.KnownSamples[0].Tag);
        Assert.Equal(1, catalog.Loads);
        Assert.Equal(1, catalog.Creates);
        Assert.Equal(1, catalog.Finds);
    }

    [Fact]
    public async Task ACacheMissFindsTagsAddedByAnotherInstance()
    {
        var catalog = new MemoryCatalog();
        using var resolver = Resolver(catalog);
        await resolver.InitializeAsync(TestContext.Current.CancellationToken);
        catalog.Tags.Add(new TagMetadata(2, "Pump01", "Torque", "Nm", 0, 100));

        var result = await resolver.ResolveAsync(Batch("Torque"), TestContext.Current.CancellationToken);

        Assert.Null(result.DeadLetterBatch);
        Assert.Equal(2, Assert.Single(result.KnownSamples).Tag.TagId);
        Assert.Equal(1, catalog.Finds);
    }

    [Fact]
    public async Task MissingTagsAreCheckedAgainInTheNextBatch()
    {
        var catalog = new MemoryCatalog();
        using var resolver = Resolver(catalog);
        Assert.NotNull((await resolver.ResolveAsync(Batch("Torque"), TestContext.Current.CancellationToken)).DeadLetterBatch);
        catalog.Tags.Add(new TagMetadata(2, "Pump01", "Torque", null, null, null));

        Assert.Null((await resolver.ResolveAsync(Batch("Torque"), TestContext.Current.CancellationToken)).DeadLetterBatch);
        Assert.Equal(2, catalog.Finds);
    }

    [Fact]
    public async Task AssetIsPartOfTheCacheKey()
    {
        var catalog = new MemoryCatalog();
        catalog.Tags.Add(new TagMetadata(2, "Tank01", "FlowRate", null, null, null));
        using var resolver = Resolver(catalog);
        var original = Batch("FlowRate");
        var tank = new TelemetryBatch(1, original.BatchId, original.Source, original.Endpoint,
            original.PlantId, original.LineId, "Tank01", original.CreatedAt, original.Samples);

        Assert.Equal(1, Assert.Single((await resolver.ResolveAsync(original, TestContext.Current.CancellationToken)).KnownSamples).Tag.TagId);
        Assert.Equal(2, Assert.Single((await resolver.ResolveAsync(tank, TestContext.Current.CancellationToken)).KnownSamples).Tag.TagId);
    }

    [Fact]
    public async Task AFailedCatalogCallDoesNotBecomeAnUnknownTag()
    {
        var catalog = new MemoryCatalog { FailFind = true };
        using var resolver = Resolver(catalog);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(Batch("Torque"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnprovisionedAssetRemainsUnresolvedUnderCreatePolicy()
    {
        var catalog = new MemoryCatalog { AllowCreate = false };
        using var resolver = Resolver(catalog, TagResolutionOptions.Create);

        var result = await resolver.ResolveAsync(Batch("Torque"), TestContext.Current.CancellationToken);

        Assert.Empty(result.KnownSamples);
        Assert.NotNull(result.DeadLetterBatch);
    }

    private static TagResolver Resolver(MemoryCatalog catalog, string policy = TagResolutionOptions.DeadLetter) =>
        new(catalog, Options.Create(new TagResolutionOptions { UnknownTagPolicy = policy }));

    private static TelemetryBatch Batch(params string[] tags)
    {
        var timestamp = new DateTimeOffset(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);
        return new(1, Guid.NewGuid(), "opcua", "opc.tcp://test:50000", "Plant01", "Line01", "Pump01",
            timestamp, tags.Select((tag, index) => new BatchSample(tag, index, timestamp, timestamp, 0)).ToArray());
    }

    private sealed class MemoryCatalog : ITagCatalog
    {
        public List<TagMetadata> Tags { get; } = [new(1, "Pump01", "FlowRate", "m3/h", 0, 80)];
        public int Loads { get; private set; }
        public int Finds { get; private set; }
        public int Creates { get; private set; }
        public bool FailFind { get; init; }
        public bool AllowCreate { get; init; } = true;

        public Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult<IReadOnlyList<TagMetadata>>(Tags.ToArray());
        }

        public Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken)
        {
            Finds++;
            if (FailFind) throw new InvalidOperationException("catalog unavailable");
            return Task.FromResult(Tags.SingleOrDefault(item => item.AssetId == assetId && item.Name == tag));
        }

        public Task<TagMetadata?> CreateAsync(string assetId, string tag, CancellationToken cancellationToken)
        {
            Creates++;
            if (!AllowCreate) return Task.FromResult<TagMetadata?>(null);
            var created = new TagMetadata(Tags.Count + 1, assetId, tag, null, null, null);
            Tags.Add(created);
            return Task.FromResult<TagMetadata?>(created);
        }
    }
}
