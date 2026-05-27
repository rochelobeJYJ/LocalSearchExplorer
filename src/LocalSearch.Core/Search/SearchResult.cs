using LocalSearch.Core.Models;

namespace LocalSearch.Core.Search;

public sealed class SearchResult
{
    public required IndexedItem Item { get; init; }
    public SearchMatchKind MatchKind { get; init; }
    public int Score { get; init; }
}
