using System.Globalization;
using LocalSearch.Core.Search;

namespace LocalSearch.App.ViewModels;

public sealed class SearchResultViewModel
{
    public SearchResultViewModel(SearchResult result, int sequenceNumber)
    {
        var item = result.Item;
        SequenceNumber = sequenceNumber;
        RootId = item.RootId;
        Name = item.Name;
        FullPath = item.FullPath;
        ParentPath = item.ParentPath;
        IsDirectory = item.IsDirectory;
        Size = item.Size;
        ModifiedAt = item.ModifiedAt;
        KindDisplay = item.IsDirectory ? "폴더" : (string.IsNullOrWhiteSpace(item.Extension) ? "파일" : item.Extension.ToUpperInvariant());
        SizeDisplay = item.IsDirectory ? string.Empty : FormatSize(item.Size);
        ModifiedDisplay = item.ModifiedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;
        MatchDisplay = result.MatchKind switch
        {
            SearchMatchKind.Content => "내용",
            SearchMatchKind.Extension => "확장자",
            SearchMatchKind.FolderName => "폴더명",
            SearchMatchKind.Modified => "수정일",
            SearchMatchKind.Path => "경로",
            SearchMatchKind.Size => "크기",
            SearchMatchKind.TypeFilter => "유형",
            _ => "파일명"
        };
    }

    public int SequenceNumber { get; }
    public string Name { get; }
    public long RootId { get; }
    public string FullPath { get; }
    public string ParentPath { get; }
    public bool IsDirectory { get; }
    public long? Size { get; }
    public DateTimeOffset? ModifiedAt { get; }
    public string KindDisplay { get; }
    public string SizeDisplay { get; }
    public string ModifiedDisplay { get; }
    public string MatchDisplay { get; }

    private static string FormatSize(long? size)
    {
        if (!size.HasValue)
        {
            return string.Empty;
        }

        var value = (double)size.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }
}
