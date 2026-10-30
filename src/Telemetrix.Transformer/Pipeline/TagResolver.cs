using Microsoft.Extensions.Options;
using Telemetrix.Contracts;

namespace Telemetrix.Transformer.Pipeline;

public sealed class TagResolver(ITagCatalog catalog, IOptions<TagResolutionOptions> options) : IDisposable
{
    private readonly Dictionary<(string Asset, string Tag), TagMetadata> cache = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool loaded;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await LoadAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<TagResolution> ResolveAsync(TelemetryBatch batch, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var policy = options.Value.UnknownTagPolicy;
            if (policy is not (TagResolutionOptions.DeadLetter or TagResolutionOptions.Create))
            {
                throw new InvalidOperationException("TagResolution:UnknownTagPolicy must be dead-letter or create.");
            }
            await LoadAsync(cancellationToken);
            var known = new List<ResolvedSample>();
            var unknown = new List<BatchSample>();
            var missing = new HashSet<(string Asset, string Tag)>();
            foreach (var sample in batch.Samples)
            {
                var key = (batch.AssetId, sample.Tag);
                if (!cache.TryGetValue(key, out var tag) && !missing.Contains(key))
                {
                    tag = await catalog.FindAsync(batch.AssetId, sample.Tag, cancellationToken);
                    if (tag is null && policy == TagResolutionOptions.Create)
                    {
                        tag = await catalog.CreateAsync(batch.AssetId, sample.Tag, cancellationToken);
                    }
                    if (tag is not null) cache[key] = tag;
                    else missing.Add(key);
                }
                if (tag is null) unknown.Add(sample);
                else known.Add(new ResolvedSample(sample, tag));
            }

            var deadLetter = unknown.Count == 0 ? null : new TelemetryBatch(batch.SchemaVersion,
                batch.BatchId, batch.Source, batch.Endpoint, batch.PlantId, batch.LineId,
                batch.AssetId, batch.CreatedAt, unknown, batch.Transformation);
            return new TagResolution(known, deadLetter);
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (loaded) return;
        var tags = await catalog.LoadAsync(cancellationToken);
        foreach (var tag in tags) cache[(tag.AssetId, tag.Name)] = tag;
        loaded = true;
    }
}
