using LocalSearch.Core.Data;
using LocalSearch.Core.Search.Query;

namespace LocalSearch.Core.Search;

public sealed class SearchService
{
    private readonly IIndexStore _store;
    private readonly QueryParser _parser = new();

    public SearchService(IIndexStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string? input,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions();
        var query = _parser.Parse(input);

        var request = new SearchRequest
        {
            Query = query,
            TypeFilter = options.TypeFilter,
            IgnoreWhitespace = options.IgnoreWhitespace,
            IncludeContent = options.IncludeContent,
            ScopePath = options.ScopePath,
            IncludeSubfolders = options.IncludeSubfolders,
            Limit = options.Limit
        };

        return await _store.SearchAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
