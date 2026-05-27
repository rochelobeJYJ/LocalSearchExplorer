namespace LocalSearch.Core.Indexing;

public sealed record IndexingProgress(int IndexedCount, int ErrorCount, string CurrentPath);
