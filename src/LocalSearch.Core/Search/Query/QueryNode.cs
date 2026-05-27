namespace LocalSearch.Core.Search.Query;

public abstract record QueryNode;

public sealed record MatchNode(SearchField Field, string Value, bool IsRegex = false, bool IsWildcard = false) : QueryNode;

public sealed record AndNode(IReadOnlyList<QueryNode> Children) : QueryNode;

public sealed record OrNode(IReadOnlyList<QueryNode> Children) : QueryNode;

public sealed record NotNode(QueryNode Child) : QueryNode;

public sealed record EmptyNode : QueryNode;
