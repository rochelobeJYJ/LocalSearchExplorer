using System.Diagnostics;
using LocalSearch.Core.Data;
using LocalSearch.Core.Models;
using LocalSearch.Core.Text;

namespace LocalSearch.Core.Content;

public sealed class ContentIndexingService
{
    private const long DefaultMaxFileSize = 50L * 1024 * 1024;
    private const int ProgressIntervalMilliseconds = 200;
    private const int ProgressItemInterval = 25;
    private readonly IIndexStore _store;
    private readonly IContentExtractor _extractor;

    public ContentIndexingService(IIndexStore store, IContentExtractor extractor)
    {
        _store = store;
        _extractor = extractor;
    }

    public async Task<ContentIndexingSummary> IndexRootContentAsync(
        long rootId,
        IProgress<ContentIndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = await _store.GetItemsForContentIndexingAsync(rootId, cancellationToken).ConfigureAwait(false);
        var completed = 0;
        var failed = 0;
        var progressWatch = Stopwatch.StartNew();
        var lastReportedProcessed = -ProgressItemInterval;

        void ReportProgress(string currentPath, bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            var processed = completed + failed;
            if (!force &&
                progressWatch.ElapsedMilliseconds < ProgressIntervalMilliseconds &&
                processed - lastReportedProcessed < ProgressItemInterval)
            {
                return;
            }

            progressWatch.Restart();
            lastReportedProcessed = processed;
            progress.Report(new ContentIndexingProgress(completed, failed, items.Count, currentPath));
        }

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(item.FullPath);

            if (item.Size.HasValue && item.Size.Value > DefaultMaxFileSize)
            {
                await _store.MarkContentIndexFailedAsync(item.Id, "TooLarge", "50MB 이상 파일은 기본 내용 인덱싱에서 건너뜁니다.", cancellationToken).ConfigureAwait(false);
                failed++;
                continue;
            }

            if (!_extractor.CanExtract(item))
            {
                await _store.MarkContentIndexFailedAsync(item.Id, "Unsupported", $"지원하지 않는 파일 형식입니다: {item.Extension}", cancellationToken).ConfigureAwait(false);
                failed++;
                continue;
            }

            try
            {
                var chunks = await _extractor.ExtractAsync(item, cancellationToken).ConfigureAwait(false);
                var normalized = chunks
                    .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
                    .Select(chunk => new ContentChunk
                    {
                        ChunkNo = chunk.ChunkNo,
                        PageNo = chunk.PageNo,
                        SheetName = chunk.SheetName,
                        Text = chunk.Text
                    })
                    .ToArray();

                await _store.ReplaceContentAsync(item.Id, normalized, cancellationToken).ConfigureAwait(false);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _store.MarkContentIndexFailedAsync(item.Id, ex.GetType().Name, ex.Message, cancellationToken).ConfigureAwait(false);
                failed++;
            }
        }

        ReportProgress(string.Empty, force: true);
        return new ContentIndexingSummary(completed, failed, items.Count);
    }
}
