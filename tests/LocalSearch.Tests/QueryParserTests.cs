using LocalSearch.Core.Search.Query;

namespace LocalSearch.Tests;

public sealed class QueryParserTests
{
    private readonly QueryParser _parser = new();

    [Theory]
    [InlineData("보고서")]
    [InlineData("보고서 & 2024")]
    [InlineData("보고서, 계약서")]
    [InlineData("계약서 & !초안")]
    [InlineData("계약서 & -초안")]
    [InlineData("(계약서, 견적서) & ext:pdf")]
    [InlineData("name:보고서")]
    [InlineData("path:인사팀")]
    [InlineData("type:file")]
    [InlineData("type:folder")]
    [InlineData("content:\"계약 금액\"")]
    [InlineData("*.pdf")]
    [InlineData("re:/^IMG_\\d+\\.jpg$/")]
    public void Parse_Accepts_Documented_Query_Syntax(string input)
    {
        var node = _parser.Parse(input);

        Assert.NotNull(node);
    }

    [Fact]
    public void Parse_Throws_When_Closing_Paren_Is_Missing()
    {
        var exception = Assert.Throws<QueryParseException>(() => _parser.Parse("계약서 & ("));

        Assert.Contains("닫는 괄호", exception.Message);
    }

    [Fact]
    public void Parse_Uses_And_Before_Or()
    {
        var node = _parser.Parse("계약서, 견적서 & ext:pdf");

        var or = Assert.IsType<OrNode>(node);
        Assert.Equal(2, or.Children.Count);
        Assert.IsType<AndNode>(or.Children[1]);
    }

    [Fact]
    public void Regex_Validation_Throws_Before_Row_Evaluation()
    {
        var node = _parser.Parse("re:/[a-/");

        Assert.Throws<System.Text.RegularExpressions.RegexParseException>(() => QueryEvaluator.ValidateRegexes(node));
    }
}
