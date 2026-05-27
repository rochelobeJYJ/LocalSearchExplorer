using LocalSearch.Core.Text;

namespace LocalSearch.Tests;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("연차 신청서", "연차신청서")]
    [InlineData("연차신청서", "연차신청서")]
    [InlineData("연 차 신 청 서", "연차신청서")]
    public void RemoveWhitespace_Makes_Korean_Space_Variants_Comparable(string input, string expected)
    {
        var normalized = TextNormalizer.Normalize(input);

        Assert.Equal(expected, TextNormalizer.RemoveWhitespace(normalized));
    }

    [Fact]
    public void Normalize_Lowercases_And_Trims()
    {
        Assert.Equal("report 2026.pdf", TextNormalizer.Normalize("  REPORT 2026.PDF  "));
    }
}
