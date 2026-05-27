using LocalSearch.Core.Models;

namespace LocalSearch.Core.Content;

public interface IContentExtractor
{
    bool CanExtract(IndexedItem item);
    Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default);
}
