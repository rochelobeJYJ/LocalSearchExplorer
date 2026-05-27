using LocalSearch.Core.Models;
using UglyToad.PdfPig;

namespace LocalSearch.Core.Content;

public sealed class PdfTextExtractor : IContentExtractor
{
    public bool CanExtract(IndexedItem item)
    {
        return !item.IsDirectory && string.Equals(item.Extension, "pdf", StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ContentChunk>>(() =>
        {
            var chunks = new List<ContentChunk>();
            using var document = PdfDocument.Open(item.FullPath);
            var chunkNo = 0;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(page.Text))
                {
                    chunks.Add(new ContentChunk
                    {
                        ChunkNo = chunkNo++,
                        PageNo = page.Number,
                        Text = page.Text
                    });
                }
            }

            return chunks;
        }, cancellationToken);
    }
}
