using System.Text;

namespace LocalSearch.Core.Search.Query;

public sealed class QueryTokenizer
{
    public IReadOnlyList<QueryToken> Tokenize(string? input)
    {
        input ??= string.Empty;
        var tokens = new List<QueryToken>();
        var index = 0;

        while (index < input.Length)
        {
            var current = input[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            switch (current)
            {
                case '&':
                    tokens.Add(new QueryToken(QueryTokenType.And, "&"));
                    index++;
                    continue;
                case ',':
                    tokens.Add(new QueryToken(QueryTokenType.Or, ","));
                    index++;
                    continue;
                case '!':
                    tokens.Add(new QueryToken(QueryTokenType.Not, "!"));
                    index++;
                    continue;
                case '-':
                    tokens.Add(new QueryToken(QueryTokenType.Not, "-"));
                    index++;
                    continue;
                case '(':
                    tokens.Add(new QueryToken(QueryTokenType.OpenParen, "("));
                    index++;
                    continue;
                case ')':
                    tokens.Add(new QueryToken(QueryTokenType.CloseParen, ")"));
                    index++;
                    continue;
                case '"':
                    tokens.Add(ReadPhrase(input, ref index));
                    continue;
                default:
                    tokens.Add(ReadWord(input, ref index));
                    continue;
            }
        }

        tokens.Add(new QueryToken(QueryTokenType.End, string.Empty));
        return tokens;
    }

    private static QueryToken ReadPhrase(string input, ref int index)
    {
        index++;
        var builder = new StringBuilder();

        while (index < input.Length)
        {
            var current = input[index];
            if (current == '"')
            {
                index++;
                return new QueryToken(QueryTokenType.Phrase, builder.ToString());
            }

            if (current == '\\' && index + 1 < input.Length)
            {
                index++;
                builder.Append(input[index]);
                index++;
                continue;
            }

            builder.Append(current);
            index++;
        }

        throw new QueryParseException("검색식 오류: 닫는 따옴표가 필요합니다.");
    }

    private static QueryToken ReadWord(string input, ref int index)
    {
        var builder = new StringBuilder();
        while (index < input.Length)
        {
            var current = input[index];
            if (char.IsWhiteSpace(current) || current is '&' or ',' or '!' or '(' or ')' or '"')
            {
                break;
            }

            if (current == '-' && builder.Length == 0)
            {
                break;
            }

            builder.Append(current);
            index++;
        }

        if (builder.Length == 0)
        {
            throw new QueryParseException($"검색식 오류: 처리할 수 없는 문자입니다. '{input[index]}'");
        }

        return new QueryToken(QueryTokenType.Word, builder.ToString());
    }
}
