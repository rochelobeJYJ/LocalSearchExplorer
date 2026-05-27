namespace LocalSearch.Core.Content;

public sealed record ContentIndexingProgress(int CompletedCount, int FailedCount, int TotalCount, string CurrentPath);
