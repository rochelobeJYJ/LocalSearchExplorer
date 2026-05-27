namespace LocalSearch.Core.Models;

public sealed class ExclusionRule
{
    public long Id { get; init; }
    public long? RootId { get; init; }
    public required string PathPattern { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
