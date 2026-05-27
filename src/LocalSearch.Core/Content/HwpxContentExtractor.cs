using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using LocalSearch.Core.Models;

namespace LocalSearch.Core.Content;

public sealed class HwpxContentExtractor : IContentExtractor
{
    private static readonly Encoding KoreanZipEncoding = CreateKoreanZipEncoding();

    public bool CanExtract(IndexedItem item)
    {
        return !item.IsDirectory && string.Equals(item.Extension, "hwpx", StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ContentChunk>>(() =>
        {
            var chunks = new List<ContentChunk>();
            using var stream = File.OpenRead(item.FullPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, KoreanZipEncoding);
            var xmlEntries = archive.Entries
                .Where(IsBodySectionXml)
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var chunkNo = 0;
            foreach (var entry in xmlEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var entryStream = entry.Open();
                var document = XDocument.Load(entryStream);
                var text = string.Join(
                    Environment.NewLine,
                    document.DescendantNodes()
                        .OfType<XText>()
                        .Select(node => node.Value.Trim())
                        .Where(value => value.Length > 0));

                if (!string.IsNullOrWhiteSpace(text))
                {
                    chunks.Add(new ContentChunk
                    {
                        ChunkNo = chunkNo++,
                        SheetName = entry.FullName,
                        Text = text
                    });
                }
            }

            return chunks;
        }, cancellationToken);
    }

    private static bool IsBodySectionXml(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        return name.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding CreateKoreanZipEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949);
    }
}
