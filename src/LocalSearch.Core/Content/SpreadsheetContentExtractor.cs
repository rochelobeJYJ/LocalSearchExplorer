using ExcelDataReader;
using LocalSearch.Core.Models;

namespace LocalSearch.Core.Content;

public sealed class SpreadsheetContentExtractor : IContentExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "xls",
        "xlsx"
    };

    static SpreadsheetContentExtractor()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public bool CanExtract(IndexedItem item)
    {
        return !item.IsDirectory && item.Extension is not null && SupportedExtensions.Contains(item.Extension);
    }

    public Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ContentChunk>>(() =>
        {
            var chunks = new List<ContentChunk>();
            using var stream = File.Open(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var chunkNo = 0;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new List<string>();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader.GetValue(i);
                        if (value is not null)
                        {
                            values.Add(Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty);
                        }
                    }
                }

                var text = string.Join(Environment.NewLine, values.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    chunks.Add(new ContentChunk
                    {
                        ChunkNo = chunkNo++,
                        SheetName = reader.Name,
                        Text = text
                    });
                }
            }
            while (reader.NextResult());

            return chunks;
        }, cancellationToken);
    }
}
