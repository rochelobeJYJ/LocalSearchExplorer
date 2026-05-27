namespace LocalSearch.Core.Indexing;

public sealed record ScanProgress(int IndexedCount, int ErrorCount, string CurrentPath);
