namespace LocalSearch.Core.Models;

public sealed class IndexedItem
{
    public long Id { get; init; }
    public long RootId { get; init; }
    public required string FullPath { get; init; }
    public required string ParentPath { get; init; }
    public required string Name { get; init; }
    public required string NormalizedName { get; init; }
    public required string NormalizedNameNoSpace { get; init; }
    public required string NormalizedPath { get; init; }
    public required string NormalizedPathNoSpace { get; init; }
    public string? Extension { get; init; }
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required string Attributes { get; init; }
    public bool IsMissing { get; init; }
}
