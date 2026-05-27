using System.Text;
using LocalSearch.Core.Models;

namespace LocalSearch.Core.Content;

public sealed class PlainTextExtractor : IContentExtractor
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding KoreanEncoding = CreateKoreanEncoding();

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt",
        "md",
        "csv",
        "log",
        "json",
        "xml"
    };

    public bool CanExtract(IndexedItem item)
    {
        return !item.IsDirectory && item.Extension is not null && SupportedExtensions.Contains(item.Extension);
    }

    public async Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(item.FullPath, cancellationToken).ConfigureAwait(false);
        var text = Decode(bytes);
        return [new ContentChunk { ChunkNo = 0, Text = text }];
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true).GetString(bytes, 4, bytes.Length - 4);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return KoreanEncoding.GetString(bytes);
        }
    }

    private static Encoding CreateKoreanEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949);
    }
}
