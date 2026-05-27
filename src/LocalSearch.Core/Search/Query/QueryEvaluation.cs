namespace LocalSearch.Core.Search.Query;

public readonly record struct QueryEvaluation(bool IsMatch, SearchMatchKind MatchKind, int Score)
{
    public static QueryEvaluation NoMatch => new(false, SearchMatchKind.Name, 0);

    public static QueryEvaluation Match(SearchMatchKind matchKind, int score)
    {
        return new QueryEvaluation(true, matchKind, score);
    }
}
