using System.IO;
using LocalSearch.Core.Models;

namespace LocalSearch.App.ViewModels;

public sealed class RootViewModel
{
    public RootViewModel(IndexRoot root)
    {
        Id = root.Id;
        Path = root.Path;
        var directoryName = new DirectoryInfo(root.Path).Name;
        DisplayName = string.IsNullOrWhiteSpace(directoryName) ? root.Path : directoryName;
        LastIndexedDisplay = root.LastIndexedAt.HasValue
            ? $"마지막 인덱싱: {root.LastIndexedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}"
            : "아직 인덱싱되지 않음";
    }

    public string Path { get; }
    public long Id { get; }
    public string DisplayName { get; }
    public string LastIndexedDisplay { get; }
}
