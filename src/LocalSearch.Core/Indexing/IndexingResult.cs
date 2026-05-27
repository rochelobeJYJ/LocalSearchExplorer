using LocalSearch.Core.Models;

namespace LocalSearch.Core.Indexing;

public sealed record IndexingResult(IndexRoot Root, int IndexedCount, int ErrorCount);
