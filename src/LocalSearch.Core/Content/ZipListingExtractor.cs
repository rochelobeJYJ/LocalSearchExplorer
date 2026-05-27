using System.IO.Compression;
using System.Text;
using LocalSearch.Core.Models;

namespace LocalSearch.Core.Content;

public sealed class ZipListingExtractor : IContentExtractor
{
    private static readonly Encoding KoreanZipEncoding = CreateKoreanZipEncoding();

    public bool CanExtract(IndexedItem item)
    {
        return !item.IsDirectory && string.Equals(item.Extension, "zip", StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ContentChunk>>(() =>
        {
            using var stream = File.OpenRead(item.FullPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, KoreanZipEncoding);
            var names = archive.Entries
                .Select(entry => entry.FullName)
                .Where(name => !string.IsNullOrWhiteSpace(name));
            return (IReadOnlyList<ContentChunk>)
            [
                new ContentChunk
                {
                    ChunkNo = 0,
                    Text = string.Join(Environment.NewLine, names)
                }
            ];
        }, cancellationToken);
    }

    private static Encoding CreateKoreanZipEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949);
    }
}
