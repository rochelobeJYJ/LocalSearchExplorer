namespace LocalSearch.Core.Models;

public sealed class IndexRoot
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public bool IsEnabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastIndexedAt { get; init; }
}
