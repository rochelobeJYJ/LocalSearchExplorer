namespace LocalSearch.Core.Search;

public sealed class SearchOptions
{
    public bool IgnoreWhitespace { get; init; } = true;
    public bool IncludeContent { get; init; }
    public ItemTypeFilter TypeFilter { get; init; } = ItemTypeFilter.All;
    public string? ScopePath { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    public int Limit { get; init; } = 500;
}
