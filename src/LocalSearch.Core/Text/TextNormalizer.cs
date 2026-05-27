using System.Globalization;
using System.Text;

namespace LocalSearch.Core.Text;

public static class TextNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Normalize(NormalizationForm.FormKC)
            .Trim()
            .ToLower(CultureInfo.InvariantCulture);
    }

    public static string RemoveWhitespace(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
