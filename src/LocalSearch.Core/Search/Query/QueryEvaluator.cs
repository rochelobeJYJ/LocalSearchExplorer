using System.Globalization;
using System.Text.RegularExpressions;
using LocalSearch.Core.Models;
using LocalSearch.Core.Text;

namespace LocalSearch.Core.Search.Query;

public sealed class QueryEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly RegexOptions SearchRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private readonly bool _ignoreWhitespace;
    private readonly Dictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);

    public QueryEvaluator(bool ignoreWhitespace, QueryNode? rootNode = null)
    {
        _ignoreWhitespace = ignoreWhitespace;
        if (rootNode is not null)
        {
            PrecompileRegexes(rootNode);
        }
    }

    public QueryEvaluation Evaluate(IndexedItem item, QueryNode node)
    {
        return Evaluate(item, node, normalizedContent: null);
    }

    public QueryEvaluation Evaluate(IndexedItem item, QueryNode node, string? normalizedContent)
    {
        if (node is EmptyNode)
        {
            return QueryEvaluation.Match(SearchMatchKind.Name, 10);
        }

        return EvaluateNode(item, node, normalizedContent);
    }

    private QueryEvaluation EvaluateNode(IndexedItem item, QueryNode node, string? normalizedContent)
    {
        return node switch
        {
            EmptyNode => QueryEvaluation.Match(SearchMatchKind.Name, 10),
            MatchNode match => EvaluateMatch(item, match, normalizedContent),
            NotNode not => EvaluateNode(item, not.Child, normalizedContent).IsMatch ? QueryEvaluation.NoMatch : QueryEvaluation.Match(SearchMatchKind.Name, 0),
            AndNode and => EvaluateAnd(item, and, normalizedContent),
            OrNode or => EvaluateOr(item, or, normalizedContent),
            _ => QueryEvaluation.NoMatch
        };
    }

    private QueryEvaluation EvaluateAnd(IndexedItem item, AndNode node, string? normalizedContent)
    {
        var score = 0;
        var matchKind = SearchMatchKind.Name;
        foreach (var child in node.Children)
        {
            var result = EvaluateNode(item, child, normalizedContent);
            if (!result.IsMatch)
            {
                return QueryEvaluation.NoMatch;
            }

            score += result.Score;
            if (result.Score > 0)
            {
                matchKind = result.MatchKind;
            }
        }

        return QueryEvaluation.Match(matchKind, score);
    }

    private QueryEvaluation EvaluateOr(IndexedItem item, OrNode node, string? normalizedContent)
    {
        QueryEvaluation best = QueryEvaluation.NoMatch;
        foreach (var child in node.Children)
        {
            var result = EvaluateNode(item, child, normalizedContent);
            if (result.IsMatch && result.Score >= best.Score)
            {
                best = result;
            }
        }

        return best;
    }

    private QueryEvaluation EvaluateMatch(IndexedItem item, MatchNode node, string? normalizedContent)
    {
        return node.Field switch
        {
            SearchField.Any => MatchAny(item, node, normalizedContent),
            SearchField.Name => MatchText(item.NormalizedName, item.NormalizedNameNoSpace, node, GetNameMatchKind(item), nameWeight: true),
            SearchField.Path => MatchText(item.NormalizedPath, item.NormalizedPathNoSpace, node, SearchMatchKind.Path),
            SearchField.Extension => MatchExtension(item, node),
            SearchField.Type => MatchType(item, node),
            SearchField.Size => MatchSize(item, node),
            SearchField.Modified => MatchModified(item, node),
            SearchField.Content => MatchContent(normalizedContent, node),
            _ => QueryEvaluation.NoMatch
        };
    }

    private QueryEvaluation MatchAny(IndexedItem item, MatchNode node, string? normalizedContent)
    {
        var name = MatchText(item.NormalizedName, item.NormalizedNameNoSpace, node, GetNameMatchKind(item), nameWeight: true);
        if (name.IsMatch)
        {
            return name;
        }

        var path = MatchText(item.NormalizedPath, item.NormalizedPathNoSpace, node, SearchMatchKind.Path);
        if (path.IsMatch)
        {
            return path;
        }

        return MatchContent(normalizedContent, node);
    }

    private QueryEvaluation MatchText(
        string normalized,
        string normalizedNoSpace,
        MatchNode node,
        SearchMatchKind kind,
        bool nameWeight = false)
    {
        if (node.Value.Length == 0)
        {
            return QueryEvaluation.Match(kind, 1);
        }

        if (node.IsRegex)
        {
            return IsRegexMatch(normalized, GetOrCreateRegex(node.Value))
                ? QueryEvaluation.Match(kind, nameWeight ? 75 : 35)
                : QueryEvaluation.NoMatch;
        }

        if (node.IsWildcard)
        {
            return IsRegexMatch(normalized, GetOrCreateRegex(CreateWildcardPattern(node.Value)))
                ? QueryEvaluation.Match(kind, nameWeight ? 75 : 35)
                : QueryEvaluation.NoMatch;
        }

        var valueNoSpace = TextNormalizer.RemoveWhitespace(node.Value);
        if (normalized.Equals(node.Value, StringComparison.Ordinal) || (_ignoreWhitespace && normalizedNoSpace.Equals(valueNoSpace, StringComparison.Ordinal)))
        {
            return QueryEvaluation.Match(kind, nameWeight ? 100 : 45);
        }

        if (normalized.StartsWith(node.Value, StringComparison.Ordinal) || (_ignoreWhitespace && normalizedNoSpace.StartsWith(valueNoSpace, StringComparison.Ordinal)))
        {
            return QueryEvaluation.Match(kind, nameWeight ? 80 : 40);
        }

        if (normalized.Contains(node.Value, StringComparison.Ordinal) || (_ignoreWhitespace && normalizedNoSpace.Contains(valueNoSpace, StringComparison.Ordinal)))
        {
            return QueryEvaluation.Match(kind, nameWeight ? 60 : 30);
        }

        return QueryEvaluation.NoMatch;
    }

    public static void ValidateRegexes(QueryNode node)
    {
        var evaluator = new QueryEvaluator(ignoreWhitespace: false);
        evaluator.PrecompileRegexes(node);
    }

    private void PrecompileRegexes(QueryNode node)
    {
        switch (node)
        {
            case MatchNode { IsRegex: true } match:
                _ = GetOrCreateRegex(match.Value);
                break;
            case MatchNode { IsWildcard: true } match:
                _ = GetOrCreateRegex(CreateWildcardPattern(match.Value));
                break;
            case AndNode andNode:
                foreach (var child in andNode.Children)
                {
                    PrecompileRegexes(child);
                }

                break;
            case OrNode orNode:
                foreach (var child in orNode.Children)
                {
                    PrecompileRegexes(child);
                }

                break;
            case NotNode notNode:
                PrecompileRegexes(notNode.Child);
                break;
        }
    }

    private Regex GetOrCreateRegex(string pattern)
    {
        if (_regexCache.TryGetValue(pattern, out var regex))
        {
            return regex;
        }

        regex = new Regex(pattern, SearchRegexOptions, RegexTimeout);
        _regexCache[pattern] = regex;
        return regex;
    }

    private static bool IsRegexMatch(string input, Regex regex)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string CreateWildcardPattern(string value)
    {
        return "^" + Regex.Escape(value).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
    }

    private static SearchMatchKind GetNameMatchKind(IndexedItem item)
    {
        return item.IsDirectory ? SearchMatchKind.FolderName : SearchMatchKind.Name;
    }

    private static QueryEvaluation MatchExtension(IndexedItem item, MatchNode node)
    {
        if (item.IsDirectory)
        {
            return QueryEvaluation.NoMatch;
        }

        return string.Equals(item.Extension, node.Value.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
            ? QueryEvaluation.Match(SearchMatchKind.Extension, 55)
            : QueryEvaluation.NoMatch;
    }

    private QueryEvaluation MatchContent(string? normalizedContent, MatchNode node)
    {
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return QueryEvaluation.NoMatch;
        }

        var normalizedNoSpace = TextNormalizer.RemoveWhitespace(normalizedContent);
        var result = MatchText(normalizedContent, normalizedNoSpace, node, SearchMatchKind.Content);
        if (!result.IsMatch)
        {
            return QueryEvaluation.NoMatch;
        }

        return QueryEvaluation.Match(SearchMatchKind.Content, 25);
    }

    private static QueryEvaluation MatchType(IndexedItem item, MatchNode node)
    {
        return node.Value switch
        {
            "file" => item.IsDirectory ? QueryEvaluation.NoMatch : QueryEvaluation.Match(SearchMatchKind.TypeFilter, 20),
            "folder" or "directory" => item.IsDirectory ? QueryEvaluation.Match(SearchMatchKind.TypeFilter, 20) : QueryEvaluation.NoMatch,
            _ => QueryEvaluation.NoMatch
        };
    }

    private static QueryEvaluation MatchSize(IndexedItem item, MatchNode node)
    {
        if (item.IsDirectory || !item.Size.HasValue)
        {
            return QueryEvaluation.NoMatch;
        }

        return CompareLong(item.Size.Value, node.Value, TryParseSize)
            ? QueryEvaluation.Match(SearchMatchKind.Size, 20)
            : QueryEvaluation.NoMatch;
    }

    private static QueryEvaluation MatchModified(IndexedItem item, MatchNode node)
    {
        if (!item.ModifiedAt.HasValue)
        {
            return QueryEvaluation.NoMatch;
        }

        var modified = item.ModifiedAt.Value.LocalDateTime.Date;
        var value = node.Value.Trim();
        if (value.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return modified == DateTime.Today ? QueryEvaluation.Match(SearchMatchKind.Modified, 20) : QueryEvaluation.NoMatch;
        }

        if (value.Equals("week", StringComparison.OrdinalIgnoreCase) || value.Equals("7d", StringComparison.OrdinalIgnoreCase))
        {
            return modified >= DateTime.Today.AddDays(-7) ? QueryEvaluation.Match(SearchMatchKind.Modified, 20) : QueryEvaluation.NoMatch;
        }

        if (value.Equals("month", StringComparison.OrdinalIgnoreCase) || value.Equals("30d", StringComparison.OrdinalIgnoreCase))
        {
            return modified >= DateTime.Today.AddDays(-30) ? QueryEvaluation.Match(SearchMatchKind.Modified, 20) : QueryEvaluation.NoMatch;
        }

        return CompareDate(modified, value)
            ? QueryEvaluation.Match(SearchMatchKind.Modified, 20)
            : QueryEvaluation.NoMatch;
    }

    private static bool CompareLong(long actual, string expression, Func<string, long?> parser)
    {
        var comparison = ReadComparison(expression);
        var expected = parser(comparison.Value);
        if (!expected.HasValue)
        {
            return false;
        }

        return comparison.Operator switch
        {
            ">=" => actual >= expected.Value,
            "<=" => actual <= expected.Value,
            ">" => actual > expected.Value,
            "<" => actual < expected.Value,
            _ => actual == expected.Value
        };
    }

    private static bool CompareDate(DateTime actual, string expression)
    {
        var comparison = ReadComparison(expression);
        if (!DateTime.TryParse(comparison.Value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var expected))
        {
            if (!DateTime.TryParse(comparison.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out expected))
            {
                return false;
            }
        }

        expected = expected.Date;
        return comparison.Operator switch
        {
            ">=" => actual >= expected,
            "<=" => actual <= expected,
            ">" => actual > expected,
            "<" => actual < expected,
            _ => actual == expected
        };
    }

    private static (string Operator, string Value) ReadComparison(string expression)
    {
        var trimmed = expression.Trim();
        foreach (var op in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (trimmed.StartsWith(op, StringComparison.Ordinal))
            {
                return (op, trimmed[op.Length..].Trim());
            }
        }

        return ("=", trimmed);
    }

    private static long? TryParseSize(string value)
    {
        var trimmed = value.Trim().ToUpperInvariant();
        var multiplier = 1L;
        foreach (var unit in new[] { "TB", "GB", "MB", "KB", "B" })
        {
            if (!trimmed.EndsWith(unit, StringComparison.Ordinal))
            {
                continue;
            }

            multiplier = unit switch
            {
                "TB" => 1024L * 1024 * 1024 * 1024,
                "GB" => 1024L * 1024 * 1024,
                "MB" => 1024L * 1024,
                "KB" => 1024L,
                _ => 1L
            };
            trimmed = trimmed[..^unit.Length].Trim();
            break;
        }

        return double.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? (long)(number * multiplier)
            : null;
    }
}
