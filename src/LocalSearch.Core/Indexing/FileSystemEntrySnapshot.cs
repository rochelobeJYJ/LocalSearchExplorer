namespace LocalSearch.Core.Indexing;

public sealed class FileSystemEntrySnapshot
{
    public required string FullPath { get; init; }
    public required string ParentPath { get; init; }
    public required string Name { get; init; }
    public string? Extension { get; init; }
    public required bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public required string Attributes { get; init; }
}
