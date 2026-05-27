using System.Text;
using DocumentFormat.OpenXml.Packaging;
using LocalSearch.Core.Models;

namespace LocalSearch.Core.Content;

public sealed class OpenXmlDocumentExtractor : IContentExtractor
{
    public bool CanExtract(IndexedItem item)
    {
        return !item.IsDirectory && string.Equals(item.Extension, "docx", StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ContentChunk>>(() =>
        {
            using var document = WordprocessingDocument.Open(item.FullPath, false);
            var mainPart = document.MainDocumentPart;
            var parts = new List<string>();
            AddText(parts, mainPart?.Document.Body?.InnerText);

            if (mainPart is not null)
            {
                foreach (var headerPart in mainPart.HeaderParts)
                {
                    AddText(parts, headerPart.Header?.InnerText);
                }

                foreach (var footerPart in mainPart.FooterParts)
                {
                    AddText(parts, footerPart.Footer?.InnerText);
                }

                AddText(parts, mainPart.FootnotesPart?.Footnotes?.InnerText);
                AddText(parts, mainPart.EndnotesPart?.Endnotes?.InnerText);
                AddText(parts, mainPart.WordprocessingCommentsPart?.Comments?.InnerText);
            }

            var text = string.Join(Environment.NewLine, parts);
            return [new ContentChunk { ChunkNo = 0, Text = NormalizeLineBreaks(text) }];
        }, cancellationToken);
    }

    private static void AddText(ICollection<string> parts, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(text);
        }
    }

    private static string NormalizeLineBreaks(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character == '\t' ? ' ' : character);
        }

        return builder.ToString();
    }
}
