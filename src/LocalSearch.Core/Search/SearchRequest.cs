using LocalSearch.Core.Search.Query;

namespace LocalSearch.Core.Search;

public sealed class SearchRequest
{
    public QueryNode Query { get; init; } = new EmptyNode();
    public ItemTypeFilter TypeFilter { get; init; } = ItemTypeFilter.All;
    public bool IgnoreWhitespace { get; init; } = true;
    public bool IncludeContent { get; init; }
    public string? ScopePath { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    public int Limit { get; init; } = 500;
}
