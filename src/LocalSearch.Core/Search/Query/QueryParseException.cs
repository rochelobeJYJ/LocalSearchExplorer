namespace LocalSearch.Core.Search.Query;

public sealed class QueryParseException : Exception
{
    public QueryParseException(string message) : base(message)
    {
    }
}
