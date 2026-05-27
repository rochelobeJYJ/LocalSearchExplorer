namespace LocalSearch.Core.Content;

public sealed class ContentChunk
{
    public int ChunkNo { get; init; }
    public int? PageNo { get; init; }
    public string? SheetName { get; init; }
    public required string Text { get; init; }
}
