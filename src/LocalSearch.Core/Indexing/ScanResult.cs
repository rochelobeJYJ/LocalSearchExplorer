namespace LocalSearch.Core.Indexing;

public sealed record ScanResult(
    string RootPath,
    IReadOnlyList<FileSystemEntrySnapshot> Entries,
    IReadOnlyList<ScanError> Errors);
