using LocalSearch.Core.Data;
using LocalSearch.Core.Models;
using LocalSearch.Core.Text;

namespace LocalSearch.Core.Indexing;

public sealed class IndexingService
{
    private readonly IIndexStore _store;
    private readonly FolderScanner _scanner;

    public IndexingService(IIndexStore store, FolderScanner scanner)
    {
        _store = store;
        _scanner = scanner;
    }

    public async Task<IndexingResult> AddOrRefreshRootAsync(
        string rootPath,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullRootPath = NormalizeRootPath(rootPath);
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var root = await _store.UpsertRootAsync(fullRootPath, cancellationToken).ConfigureAwait(false);
        var exclusions = await _store.GetExclusionsAsync(root.Id, cancellationToken).ConfigureAwait(false);

        var scanProgress = new Progress<ScanProgress>(value =>
        {
            progress?.Report(new IndexingProgress(value.IndexedCount, value.ErrorCount, value.CurrentPath));
        });

        var scanResult = await _scanner.ScanAsync(
            fullRootPath,
            exclusions.Select(exclusion => exclusion.PathPattern).ToArray(),
            scanProgress,
            cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var items = scanResult.Entries.Select(entry => ToIndexedItem(root.Id, entry, now)).ToArray();

        await _store.ReplaceItemsForRootAsync(root.Id, items, cancellationToken).ConfigureAwait(false);
        var updatedRoot = await _store.MarkRootIndexedAsync(root.Id, now, cancellationToken).ConfigureAwait(false);

        return new IndexingResult(updatedRoot, items.Length, scanResult.Errors.Count);
    }

    private static IndexedItem ToIndexedItem(long rootId, FileSystemEntrySnapshot entry, DateTimeOffset lastSeenAt)
    {
        var normalizedName = TextNormalizer.Normalize(entry.Name);
        var normalizedPath = TextNormalizer.Normalize(entry.FullPath);

        return new IndexedItem
        {
            RootId = rootId,
            FullPath = entry.FullPath,
            ParentPath = entry.ParentPath,
            Name = entry.Name,
            NormalizedName = normalizedName,
            NormalizedNameNoSpace = TextNormalizer.RemoveWhitespace(normalizedName),
            NormalizedPath = normalizedPath,
            NormalizedPathNoSpace = TextNormalizer.RemoveWhitespace(normalizedPath),
            Extension = entry.Extension,
            IsDirectory = entry.IsDirectory,
            Size = entry.Size,
            CreatedAt = entry.CreatedAt,
            ModifiedAt = entry.ModifiedAt,
            LastSeenAt = lastSeenAt,
            Attributes = entry.Attributes,
            IsMissing = false
        };
    }

    private static string NormalizeRootPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }
}
