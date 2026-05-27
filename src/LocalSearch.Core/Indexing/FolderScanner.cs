namespace LocalSearch.Core.Indexing;

public sealed class FolderScanner
{
    public Task<ScanResult> ScanAsync(
        string rootPath,
        IReadOnlyList<string>? exclusions = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Scan(rootPath, exclusions ?? Array.Empty<string>(), progress, cancellationToken), cancellationToken);
    }

    private static ScanResult Scan(
        string rootPath,
        IReadOnlyList<string> exclusions,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"검색 대상 폴더를 찾을 수 없습니다: {normalizedRoot}");
        }

        var entries = new List<FileSystemEntrySnapshot>();
        var errors = new List<ScanError>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(normalizedRoot);

        AddSnapshot(new DirectoryInfo(normalizedRoot), entries, errors);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDirectory = pendingDirectories.Pop();
            foreach (var directory in EnumerateDirectories(currentDirectory, errors))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExcluded(directory.FullName, exclusions))
                {
                    continue;
                }

                var snapshot = AddSnapshot(directory, entries, errors);
                if (snapshot is null)
                {
                    continue;
                }

                if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pendingDirectories.Push(directory.FullName);
                }

                progress?.Report(new ScanProgress(entries.Count, errors.Count, directory.FullName));
            }

            foreach (var file in EnumerateFiles(currentDirectory, errors))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExcluded(file.FullName, exclusions))
                {
                    continue;
                }

                AddSnapshot(file, entries, errors);
                progress?.Report(new ScanProgress(entries.Count, errors.Count, file.FullName));
            }
        }

        return new ScanResult(normalizedRoot, entries, errors.ToArray());
    }

    private static IEnumerable<DirectoryInfo> EnumerateDirectories(string path, ICollection<ScanError> errors)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories().ToArray();
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            errors.Add(new ScanError(path, ex.GetType().Name, ex.Message));
            return Array.Empty<DirectoryInfo>();
        }
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string path, ICollection<ScanError> errors)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles().ToArray();
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            errors.Add(new ScanError(path, ex.GetType().Name, ex.Message));
            return Array.Empty<FileInfo>();
        }
    }

    private static FileSystemEntrySnapshot? AddSnapshot(
        FileSystemInfo info,
        ICollection<FileSystemEntrySnapshot> entries,
        ICollection<ScanError> errors)
    {
        try
        {
            info.Refresh();
            var isDirectory = info is DirectoryInfo;
            var parentPath = isDirectory
                ? ((DirectoryInfo)info).Parent?.FullName ?? string.Empty
                : ((FileInfo)info).DirectoryName ?? string.Empty;

            var snapshot = new FileSystemEntrySnapshot
            {
                FullPath = info.FullName,
                ParentPath = parentPath,
                Name = info.Name,
                Extension = isDirectory ? null : NormalizeExtension(info.Extension),
                IsDirectory = isDirectory,
                Size = isDirectory ? null : ((FileInfo)info).Length,
                CreatedAt = info.CreationTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                ModifiedAt = info.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Attributes = info.Attributes.ToString()
            };

            entries.Add(snapshot);
            return snapshot;
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            errors.Add(new ScanError(info.FullName, ex.GetType().Name, ex.Message));
            return null;
        }
    }

    private static string? NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        return extension.TrimStart('.').ToLowerInvariant();
    }

    private static bool IsFileSystemException(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or PathTooLongException
            or DirectoryNotFoundException
            or FileNotFoundException
            or NotSupportedException;
    }

    private static bool IsExcluded(string fullPath, IReadOnlyList<string> exclusions)
    {
        if (exclusions.Count == 0)
        {
            return false;
        }

        var normalizedPath = NormalizePath(fullPath);
        var segments = normalizedPath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var exclusion in exclusions)
        {
            if (string.IsNullOrWhiteSpace(exclusion))
            {
                continue;
            }

            var pattern = exclusion.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathFullyQualified(pattern))
            {
                if (IsSamePathOrChild(normalizedPath, NormalizePath(pattern)))
                {
                    return true;
                }
            }
            else if (IsRelativePatternMatch(segments, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRelativePatternMatch(IReadOnlyList<string> pathSegments, string pattern)
    {
        var normalizedPattern = pattern.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedPattern.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var patternSegments = normalizedPattern
                .Split([Path.DirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (patternSegments.Length == 0 || patternSegments.Length > pathSegments.Count)
            {
                return false;
            }

            for (var start = 0; start <= pathSegments.Count - patternSegments.Length; start++)
            {
                var matched = true;
                for (var offset = 0; offset < patternSegments.Length; offset++)
                {
                    if (!string.Equals(pathSegments[start + offset], patternSegments[offset], StringComparison.OrdinalIgnoreCase))
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return true;
                }
            }

            return false;
        }

        return pathSegments.Any(segment => string.Equals(segment, normalizedPattern, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSamePathOrChild(string path, string root)
    {
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
