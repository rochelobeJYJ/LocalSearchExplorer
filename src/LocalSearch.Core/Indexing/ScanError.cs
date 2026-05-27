namespace LocalSearch.Core.Indexing;

public sealed record ScanError(string Path, string ErrorType, string Message);
