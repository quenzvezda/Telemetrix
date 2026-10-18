namespace Telemetrix.Transformer.Pipeline;

public interface ITagCatalog
{
    Task<IReadOnlyList<TagMetadata>> LoadAsync(CancellationToken cancellationToken);
    Task<TagMetadata?> FindAsync(string assetId, string tag, CancellationToken cancellationToken);
    Task<TagMetadata?> CreateAsync(string assetId, string tag, CancellationToken cancellationToken);
}
