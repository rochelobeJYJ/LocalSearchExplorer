namespace LocalSearch.Core.Models;

public sealed class RootIndexStatus
{
    public required IndexRoot Root { get; init; }
    public int ItemCount { get; init; }
    public int FolderCount { get; init; }
    public int FileCount { get; init; }
    public int ContentIndexedCount { get; init; }
    public int ContentFailedCount { get; init; }
    public int ContentPendingCount { get; init; }
}
