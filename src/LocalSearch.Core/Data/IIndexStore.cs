using LocalSearch.Core.Models;
using LocalSearch.Core.Search;
using LocalSearch.Core.Content;

namespace LocalSearch.Core.Data;

public interface IIndexStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IndexRoot> UpsertRootAsync(string path, CancellationToken cancellationToken = default);
    Task<IndexRoot> MarkRootIndexedAsync(long rootId, DateTimeOffset indexedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IndexRoot>> GetRootsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RootIndexStatus>> GetRootIndexStatusesAsync(CancellationToken cancellationToken = default);
    Task RemoveRootAsync(long rootId, CancellationToken cancellationToken = default);
    Task DeleteAllIndexesAsync(CancellationToken cancellationToken = default);
    Task DeleteContentIndexesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExclusionRule>> GetExclusionsAsync(long? rootId = null, CancellationToken cancellationToken = default);
    Task AddExclusionAsync(long? rootId, string pathPattern, string? reason = null, CancellationToken cancellationToken = default);
    Task ReplaceItemsForRootAsync(long rootId, IReadOnlyList<IndexedItem> items, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IndexedItem>> GetChildFoldersAsync(long rootId, string parentPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IndexedItem>> GetItemsForContentIndexingAsync(long rootId, CancellationToken cancellationToken = default);
    Task ReplaceContentAsync(long itemId, IReadOnlyList<ContentChunk> chunks, CancellationToken cancellationToken = default);
    Task MarkContentIndexFailedAsync(long itemId, string errorType, string message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
    Task<int> CountItemsAsync(string? scopePath = null, bool includeSubfolders = true, CancellationToken cancellationToken = default);
}
