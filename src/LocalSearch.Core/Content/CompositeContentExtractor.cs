using LocalSearch.Core.Models;

namespace LocalSearch.Core.Content;

public sealed class CompositeContentExtractor : IContentExtractor
{
    private readonly IReadOnlyList<IContentExtractor> _extractors;

    public CompositeContentExtractor(IEnumerable<IContentExtractor> extractors)
    {
        _extractors = extractors.ToArray();
    }

    public static CompositeContentExtractor CreateDefault()
    {
        return new CompositeContentExtractor(
        [
            new PlainTextExtractor(),
            new PdfTextExtractor(),
            new OpenXmlDocumentExtractor(),
            new SpreadsheetContentExtractor(),
            new HwpxContentExtractor(),
            new ZipListingExtractor()
        ]);
    }

    public bool CanExtract(IndexedItem item)
    {
        return _extractors.Any(extractor => extractor.CanExtract(item));
    }

    public async Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
    {
        var extractor = _extractors.FirstOrDefault(candidate => candidate.CanExtract(item));
        if (extractor is null)
        {
            throw new ContentExtractionException($"지원하지 않는 파일 형식입니다: {item.Extension ?? "(확장자 없음)"}");
        }

        return await extractor.ExtractAsync(item, cancellationToken).ConfigureAwait(false);
    }
}
