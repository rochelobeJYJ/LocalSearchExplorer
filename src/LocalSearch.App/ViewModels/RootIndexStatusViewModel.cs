using System.Globalization;
using LocalSearch.Core.Models;

namespace LocalSearch.App.ViewModels;

public sealed class RootIndexStatusViewModel
{
    public RootIndexStatusViewModel(RootIndexStatus status)
    {
        Id = status.Root.Id;
        Path = status.Root.Path;
        DisplayName = System.IO.Path.GetFileName(status.Root.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = status.Root.Path;
        }

        ItemCount = status.ItemCount;
        FileCount = status.FileCount;
        FolderCount = status.FolderCount;
        ContentIndexedCount = status.ContentIndexedCount;
        ContentFailedCount = status.ContentFailedCount;
        ContentPendingCount = status.ContentPendingCount;
        LastIndexedDisplay = status.Root.LastIndexedAt.HasValue
            ? status.Root.LastIndexedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : "없음";
        Summary = $"항목 {ItemCount:N0}개 | 파일 {FileCount:N0}개 | 폴더 {FolderCount:N0}개";
        ContentSummary = $"내용 인덱스 {ContentIndexedCount:N0}개 | 실패 {ContentFailedCount:N0}개 | 대기 {ContentPendingCount:N0}개";
    }

    public long Id { get; }
    public string Path { get; }
    public string DisplayName { get; }
    public int ItemCount { get; }
    public int FileCount { get; }
    public int FolderCount { get; }
    public int ContentIndexedCount { get; }
    public int ContentFailedCount { get; }
    public int ContentPendingCount { get; }
    public string LastIndexedDisplay { get; }
    public string Summary { get; }
    public string ContentSummary { get; }
}
