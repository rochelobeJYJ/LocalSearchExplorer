namespace LocalSearch.Core.Search.Query;

public enum QueryTokenType
{
    Word,
    Phrase,
    And,
    Or,
    Not,
    OpenParen,
    CloseParen,
    End
}
