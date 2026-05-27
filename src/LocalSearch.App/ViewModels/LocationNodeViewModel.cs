using System.Collections.ObjectModel;
using System.IO;
using LocalSearch.Core.Models;

namespace LocalSearch.App.ViewModels;

public sealed class LocationNodeViewModel
{
    private LocationNodeViewModel(
        string displayName,
        string detail,
        long? rootId,
        string? path,
        bool isAllLocations,
        bool isRoot,
        bool isPlaceholder)
    {
        DisplayName = displayName;
        Detail = detail;
        RootId = rootId;
        Path = path;
        IsAllLocations = isAllLocations;
        IsRoot = isRoot;
        IsPlaceholder = isPlaceholder;
    }

    public ObservableCollection<LocationNodeViewModel> Children { get; } = new();
    public string DisplayName { get; }
    public string Detail { get; }
    public long? RootId { get; }
    public string? Path { get; }
    public bool IsAllLocations { get; }
    public bool IsRoot { get; }
    public bool IsPlaceholder { get; }
    public bool HasLoadedChildren { get; set; }

    public string ScopeLabel => IsAllLocations
        ? "모든 검색 위치"
        : Path ?? DisplayName;

    public string Breadcrumb => IsAllLocations || string.IsNullOrWhiteSpace(Path)
        ? "검색 위치 > 모든 검색 위치"
        : "검색 위치 > " + Path.Replace(PathSeparator, " > ", StringComparison.Ordinal);

    public static LocationNodeViewModel AllLocations(int rootCount)
    {
        return new LocationNodeViewModel(
            "모든 검색 위치",
            rootCount == 0 ? "등록된 위치 없음" : $"등록된 루트 {rootCount:N0}개",
            rootId: null,
            path: null,
            isAllLocations: true,
            isRoot: false,
            isPlaceholder: false)
        {
            HasLoadedChildren = true
        };
    }

    public static LocationNodeViewModel FromRoot(RootIndexStatus status)
    {
        var root = status.Root;
        var displayName = GetDisplayName(root.Path);
        var node = new LocationNodeViewModel(
            displayName,
            $"{status.ItemCount:N0}개 항목 | {FormatLastIndexed(root.LastIndexedAt)}",
            root.Id,
            root.Path,
            isAllLocations: false,
            isRoot: true,
            isPlaceholder: false);
        node.AddLoadingPlaceholder();
        return node;
    }

    public static LocationNodeViewModel FromFolder(IndexedItem item)
    {
        var node = new LocationNodeViewModel(
            item.Name,
            item.FullPath,
            item.RootId,
            item.FullPath,
            isAllLocations: false,
            isRoot: false,
            isPlaceholder: false);
        node.AddLoadingPlaceholder();
        return node;
    }

    public void AddLoadingPlaceholder()
    {
        if (HasLoadedChildren || Children.Count > 0)
        {
            return;
        }

        Children.Add(new LocationNodeViewModel(
            "불러오는 중...",
            string.Empty,
            rootId: null,
            path: null,
            isAllLocations: false,
            isRoot: false,
            isPlaceholder: true));
    }

    private static string GetDisplayName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string FormatLastIndexed(DateTimeOffset? value)
    {
        return value.HasValue
            ? value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : "아직 인덱싱되지 않음";
    }

    private static string PathSeparator => System.IO.Path.DirectorySeparatorChar.ToString();
}
