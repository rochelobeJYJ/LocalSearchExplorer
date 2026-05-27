using LocalSearch.Core.Text;

namespace LocalSearch.Core.Search.Query;

public sealed class QueryParser
{
    private readonly QueryTokenizer _tokenizer = new();
    private IReadOnlyList<QueryToken> _tokens = Array.Empty<QueryToken>();
    private int _position;

    public QueryNode Parse(string? input)
    {
        _tokens = _tokenizer.Tokenize(input);
        _position = 0;
        var node = ParseOr();

        if (Current.Type != QueryTokenType.End)
        {
            throw new QueryParseException($"검색식 오류: 예상하지 못한 토큰입니다. '{Current.Text}'");
        }

        return node;
    }

    private QueryNode ParseOr()
    {
        var children = new List<QueryNode> { ParseAnd() };
        while (Match(QueryTokenType.Or))
        {
            children.Add(ParseAnd());
        }

        return children.Count == 1 ? children[0] : new OrNode(FlattenOr(children));
    }

    private QueryNode ParseAnd()
    {
        var children = new List<QueryNode> { ParseUnary() };

        while (true)
        {
            if (Match(QueryTokenType.And))
            {
                children.Add(ParseUnary());
                continue;
            }

            if (Current.Type is QueryTokenType.Word or QueryTokenType.Phrase or QueryTokenType.Not or QueryTokenType.OpenParen)
            {
                children.Add(ParseUnary());
                continue;
            }

            break;
        }

        return children.Count == 1 ? children[0] : new AndNode(FlattenAnd(children));
    }

    private QueryNode ParseUnary()
    {
        if (Match(QueryTokenType.Not))
        {
            return new NotNode(ParseUnary());
        }

        return ParsePrimary();
    }

    private QueryNode ParsePrimary()
    {
        if (Match(QueryTokenType.OpenParen))
        {
            var node = ParseOr();
            if (!Match(QueryTokenType.CloseParen))
            {
                throw new QueryParseException("검색식 오류: 닫는 괄호가 필요합니다.");
            }

            return node;
        }

        if (Current.Type == QueryTokenType.Phrase)
        {
            var phrase = Advance().Text;
            return new MatchNode(SearchField.Any, TextNormalizer.Normalize(phrase));
        }

        if (Current.Type == QueryTokenType.Word)
        {
            var word = Advance().Text;
            if (word.EndsWith(':') && Current.Type is QueryTokenType.Word or QueryTokenType.Phrase)
            {
                word += Advance().Text;
            }

            return ParseWord(word);
        }

        if (Current.Type == QueryTokenType.End)
        {
            return new EmptyNode();
        }

        throw new QueryParseException($"검색식 오류: 검색어가 필요합니다. '{Current.Text}'");
    }

    private static QueryNode ParseWord(string text)
    {
        if (text.StartsWith("re:/", StringComparison.OrdinalIgnoreCase) && text.EndsWith("/", StringComparison.Ordinal) && text.Length > 4)
        {
            return new MatchNode(SearchField.Any, text[4..^1], IsRegex: true);
        }

        if (text.StartsWith("*.", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
        {
            return CreateExtensionNode(text[2..]);
        }

        var colonIndex = text.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex > 0)
        {
            var field = text[..colonIndex].ToLowerInvariant();
            var value = text[(colonIndex + 1)..];
            return field switch
            {
                "name" => CreateMatch(SearchField.Name, value),
                "path" => CreateMatch(SearchField.Path, value),
                "content" => CreateMatch(SearchField.Content, TrimOptionalQuotes(value)),
                "ext" => CreateExtensionNode(value),
                "type" => CreateMatch(SearchField.Type, value),
                "size" => CreateMatch(SearchField.Size, value),
                "modified" => CreateMatch(SearchField.Modified, value),
                _ => CreateMatch(SearchField.Any, text)
            };
        }

        return CreateMatch(SearchField.Any, text, text.Contains('*', StringComparison.Ordinal));
    }

    private static QueryNode CreateExtensionNode(string value)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.TrimStart('.').ToLowerInvariant())
            .Where(part => part.Length > 0)
            .Select(part => (QueryNode)new MatchNode(SearchField.Extension, part))
            .ToArray();

        return parts.Length switch
        {
            0 => new EmptyNode(),
            1 => parts[0],
            _ => new OrNode(parts)
        };
    }

    private static MatchNode CreateMatch(SearchField field, string value, bool wildcard = false)
    {
        return new MatchNode(field, TextNormalizer.Normalize(TrimOptionalQuotes(value)), IsWildcard: wildcard);
    }

    private static string TrimOptionalQuotes(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
    }

    private static IReadOnlyList<QueryNode> FlattenAnd(IEnumerable<QueryNode> nodes)
    {
        return nodes.SelectMany(node => node is AndNode andNode ? andNode.Children : [node]).ToArray();
    }

    private static IReadOnlyList<QueryNode> FlattenOr(IEnumerable<QueryNode> nodes)
    {
        return nodes.SelectMany(node => node is OrNode orNode ? orNode.Children : [node]).ToArray();
    }

    private bool Match(QueryTokenType type)
    {
        if (Current.Type != type)
        {
            return false;
        }

        _position++;
        return true;
    }

    private QueryToken Advance()
    {
        return _tokens[_position++];
    }

    private QueryToken Current => _tokens[Math.Min(_position, _tokens.Count - 1)];
}
