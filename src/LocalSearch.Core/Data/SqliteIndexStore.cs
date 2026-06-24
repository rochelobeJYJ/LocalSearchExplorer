using System.Data;
using System.Globalization;
using System.Text;
using LocalSearch.Core.Models;
using LocalSearch.Core.Search;
using LocalSearch.Core.Search.Query;
using LocalSearch.Core.Content;
using LocalSearch.Core.Text;
using Microsoft.Data.Sqlite;

namespace LocalSearch.Core.Data;

public sealed class SqliteIndexStore : IIndexStore
{
    private static readonly TimeSpan MissingItemRetention = TimeSpan.FromDays(7);

    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private volatile bool _initialized;
    private volatile bool _ftsAvailable;

    public SqliteIndexStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var statement in SchemaStatements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _ftsAvailable = await TryEnsureFtsTablesAsync(connection, cancellationToken).ConfigureAwait(false);
            if (_ftsAvailable)
            {
                await BackfillItemFtsIfStaleAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<IndexRoot> UpsertRootAsync(string path, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedPath = NormalizeRootPath(path);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureRootDoesNotOverlapAsync(connection, normalizedPath, cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO roots (path, is_enabled, include_subfolders, include_hidden, include_system, enable_content_index, created_at, updated_at)
                VALUES ($path, 1, 1, 0, 0, 0, $now, $now)
                ON CONFLICT(path) DO UPDATE SET
                    is_enabled = 1,
                    updated_at = $now;
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$now", ToSql(now));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetRootByPathAsync(connection, normalizedPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IndexRoot> MarkRootIndexedAsync(
        long rootId,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE roots
                SET updated_at = $now,
                    last_indexed_at = $now
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", rootId);
            command.Parameters.AddWithValue("$now", ToSql(indexedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetRootByIdAsync(connection, rootId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexRoot>> GetRootsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, path, is_enabled, created_at, updated_at, last_indexed_at
            FROM roots
            ORDER BY path COLLATE NOCASE;
            """;

        var roots = new List<IndexRoot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roots.Add(ReadRoot(reader));
        }

        return roots;
    }

    public async Task<IReadOnlyList<RootIndexStatus>> GetRootIndexStatusesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                r.id,
                r.path,
                r.is_enabled,
                r.created_at,
                r.updated_at,
                r.last_indexed_at,
                COUNT(i.id) AS item_count,
                SUM(CASE WHEN i.is_directory = 1 THEN 1 ELSE 0 END) AS folder_count,
                SUM(CASE WHEN i.is_directory = 0 THEN 1 ELSE 0 END) AS file_count,
                SUM(CASE WHEN i.content_index_status IN ('indexed', 'indexed_empty') THEN 1 ELSE 0 END) AS content_indexed_count,
                SUM(CASE WHEN i.content_index_status = 'failed' THEN 1 ELSE 0 END) AS content_failed_count,
                SUM(CASE WHEN i.is_directory = 0 AND i.content_index_status = 'not_requested' THEN 1 ELSE 0 END) AS content_pending_count
            FROM roots r
            LEFT JOIN items i ON i.root_id = r.id AND i.is_missing = 0
            GROUP BY r.id, r.path, r.is_enabled, r.created_at, r.updated_at, r.last_indexed_at
            ORDER BY r.path COLLATE NOCASE;
            """;

        var statuses = new List<RootIndexStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            statuses.Add(new RootIndexStatus
            {
                Root = ReadRoot(reader),
                ItemCount = ReadInt32(reader, 6),
                FolderCount = ReadInt32(reader, 7),
                FileCount = ReadInt32(reader, 8),
                ContentIndexedCount = ReadInt32(reader, 9),
                ContentFailedCount = ReadInt32(reader, 10),
                ContentPendingCount = ReadInt32(reader, 11)
            });
        }

        return statuses;
    }

    public async Task RemoveRootAsync(long rootId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (_ftsAvailable)
        {
            await ExecuteNonQueryAsync(
                connection,
                (SqliteTransaction)transaction,
                "DELETE FROM item_fts WHERE item_id IN (SELECT id FROM items WHERE root_id = $root_id);",
                ("$root_id", rootId),
                cancellationToken).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM content_index WHERE item_id IN (SELECT id FROM items WHERE root_id = $root_id);", ("$root_id", rootId), cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM extraction_errors WHERE item_id IN (SELECT id FROM items WHERE root_id = $root_id);", ("$root_id", rootId), cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM duplicate_candidates WHERE item_id IN (SELECT id FROM items WHERE root_id = $root_id);", ("$root_id", rootId), cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM items WHERE root_id = $root_id;", ("$root_id", rootId), cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM exclusions WHERE root_id = $root_id;", ("$root_id", rootId), cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM roots WHERE id = $root_id;", ("$root_id", rootId), cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAllIndexesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (_ftsAvailable)
        {
            await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM item_fts;", null, cancellationToken).ConfigureAwait(false);
        }

        foreach (var table in new[] { "content_index", "extraction_errors", "duplicate_candidates", "items", "exclusions", "roots" })
        {
            await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, $"DELETE FROM {table};", null, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteContentIndexesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM content_index;", null, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM extraction_errors;", null, cancellationToken).ConfigureAwait(false);
        await TryDropLegacyContentFtsAsync(connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "UPDATE items SET content_index_status = 'not_requested';", null, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExclusionRule>> GetExclusionsAsync(
        long? rootId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        if (rootId.HasValue)
        {
            command.CommandText = """
                SELECT id, root_id, path_pattern, reason, created_at
                FROM exclusions
                WHERE root_id IS NULL OR root_id = $root_id
                ORDER BY path_pattern COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$root_id", rootId.Value);
        }
        else
        {
            command.CommandText = """
                SELECT id, root_id, path_pattern, reason, created_at
                FROM exclusions
                ORDER BY path_pattern COLLATE NOCASE;
                """;
        }

        var exclusions = new List<ExclusionRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            exclusions.Add(new ExclusionRule
            {
                Id = reader.GetInt64(0),
                RootId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                PathPattern = reader.GetString(2),
                Reason = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = FromSql(reader.GetString(4))
            });
        }

        return exclusions;
    }

    public async Task AddExclusionAsync(
        long? rootId,
        string pathPattern,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO exclusions (root_id, path_pattern, reason, created_at)
            VALUES ($root_id, $path_pattern, $reason, $created_at);
            """;
        command.Parameters.AddWithValue("$root_id", rootId.HasValue ? rootId.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("$path_pattern", pathPattern);
        command.Parameters.AddWithValue("$reason", ToDbValue(reason));
        command.Parameters.AddWithValue("$created_at", ToSql(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceItemsForRootAsync(
        long rootId,
        IReadOnlyList<IndexedItem> items,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var scanStartedAt = items.Count == 0
            ? DateTimeOffset.UtcNow
            : items.Min(item => item.LastSeenAt);

        // Build the upsert command once and reuse the prepared statement for every
        // row. The previous code created a fresh command (re-parsing this large SQL)
        // for each of potentially 100k+ items, which dominated re-index time.
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = (SqliteTransaction)transaction;
        insertCommand.CommandText = """
                INSERT INTO items (
                    root_id,
                    full_path,
                    parent_path,
                    name,
                    normalized_name,
                    normalized_name_no_space,
                    normalized_path,
                    normalized_path_no_space,
                    extension,
                    is_directory,
                    size,
                    created_at,
                    modified_at,
                    last_seen_at,
                    attributes,
                    content_index_status,
                    hash_status,
                    is_missing
                )
                VALUES (
                    $root_id,
                    $full_path,
                    $parent_path,
                    $name,
                    $normalized_name,
                    $normalized_name_no_space,
                    $normalized_path,
                    $normalized_path_no_space,
                    $extension,
                    $is_directory,
                    $size,
                    $created_at,
                    $modified_at,
                    $last_seen_at,
                    $attributes,
                    'not_requested',
                    'not_requested',
                    0
                )
                ON CONFLICT(full_path) DO UPDATE SET
                    root_id = excluded.root_id,
                    parent_path = excluded.parent_path,
                    name = excluded.name,
                    normalized_name = excluded.normalized_name,
                    normalized_name_no_space = excluded.normalized_name_no_space,
                    normalized_path = excluded.normalized_path,
                    normalized_path_no_space = excluded.normalized_path_no_space,
                    extension = excluded.extension,
                    is_directory = excluded.is_directory,
                    size = excluded.size,
                    created_at = excluded.created_at,
                    modified_at = excluded.modified_at,
                    last_seen_at = excluded.last_seen_at,
                    attributes = excluded.attributes,
                    content_index_status = CASE
                        WHEN items.is_directory IS NOT excluded.is_directory
                          OR items.size IS NOT excluded.size
                          OR items.modified_at IS NOT excluded.modified_at
                        THEN 'not_requested'
                        ELSE items.content_index_status
                    END,
                    hash_status = CASE
                        WHEN items.is_directory IS NOT excluded.is_directory
                          OR items.size IS NOT excluded.size
                          OR items.modified_at IS NOT excluded.modified_at
                        THEN 'not_requested'
                        ELSE items.hash_status
                    END,
                    is_missing = 0;
                """;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            insertCommand.Parameters.Clear();
            AddItemParameters(insertCommand, item);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var missingCommand = connection.CreateCommand())
        {
            missingCommand.Transaction = (SqliteTransaction)transaction;
            missingCommand.CommandText = """
                UPDATE items
                SET is_missing = 1
                WHERE root_id = $root_id
                  AND last_seen_at < $scan_started_at;
                """;
            missingCommand.Parameters.AddWithValue("$root_id", rootId);
            missingCommand.Parameters.AddWithValue("$scan_started_at", ToSql(scanStartedAt));
            await missingCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cleanupMissingCommand = connection.CreateCommand())
        {
            cleanupMissingCommand.Transaction = (SqliteTransaction)transaction;
            var cleanupBefore = DateTimeOffset.UtcNow - MissingItemRetention;
            // item_fts is fully rebuilt from the surviving (is_missing = 0) rows by
            // RebuildItemFtsForRootAsync below, so deleted and missing rows drop out
            // of the index automatically — no separate per-row FTS cleanup needed.
            cleanupMissingCommand.CommandText = """
                DELETE FROM items
                WHERE root_id = $root_id
                  AND is_missing = 1
                  AND last_seen_at < $cleanup_before;
                """;
            cleanupMissingCommand.Parameters.AddWithValue("$root_id", rootId);
            cleanupMissingCommand.Parameters.AddWithValue("$cleanup_before", ToSql(cleanupBefore));
            await cleanupMissingCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_ftsAvailable)
        {
            await RebuildItemFtsForRootAsync(connection, (SqliteTransaction)transaction, rootId, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexedItem>> GetItemsForContentIndexingAsync(
        long rootId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                root_id,
                full_path,
                parent_path,
                name,
                normalized_name,
                normalized_name_no_space,
                normalized_path,
                normalized_path_no_space,
                extension,
                is_directory,
                size,
                created_at,
                modified_at,
                last_seen_at,
                attributes,
                is_missing
            FROM items
            WHERE root_id = $root_id
              AND is_missing = 0
              AND is_directory = 0
              AND content_index_status IN ('not_requested', 'failed')
            ORDER BY size ASC, name COLLATE NOCASE ASC;
            """;
        command.Parameters.AddWithValue("$root_id", rootId);

        var items = new List<IndexedItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<IndexedItem>> GetChildFoldersAsync(
        long rootId,
        string parentPath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                root_id,
                full_path,
                parent_path,
                name,
                normalized_name,
                normalized_name_no_space,
                normalized_path,
                normalized_path_no_space,
                extension,
                is_directory,
                size,
                created_at,
                modified_at,
                last_seen_at,
                attributes,
                is_missing
            FROM items
            WHERE root_id = $root_id
              AND parent_path = $parent_path
              AND is_missing = 0
              AND is_directory = 1
            ORDER BY name COLLATE NOCASE ASC;
            """;
        command.Parameters.AddWithValue("$root_id", rootId);
        command.Parameters.AddWithValue("$parent_path", NormalizeScopePath(parentPath) ?? parentPath);

        var items = new List<IndexedItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task ReplaceContentAsync(
        long itemId,
        IReadOnlyList<ContentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = "DELETE FROM content_index WHERE item_id = $item_id;";
            deleteCommand.Parameters.AddWithValue("$item_id", itemId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteErrorCommand = connection.CreateCommand())
        {
            deleteErrorCommand.Transaction = (SqliteTransaction)transaction;
            deleteErrorCommand.CommandText = "DELETE FROM extraction_errors WHERE item_id = $item_id;";
            deleteErrorCommand.Parameters.AddWithValue("$item_id", itemId);
            await deleteErrorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk.Text))
            {
                continue;
            }

            var normalizedContent = TextNormalizer.Normalize(chunk.Text);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = (SqliteTransaction)transaction;
            insertCommand.CommandText = """
                INSERT INTO content_index (
                    item_id,
                    chunk_no,
                    page_no,
                    sheet_name,
                    content_text,
                    normalized_content,
                    created_at
                )
                VALUES (
                    $item_id,
                    $chunk_no,
                    $page_no,
                    $sheet_name,
                    $content_text,
                    $normalized_content,
                    $created_at
                );
                """;
            insertCommand.Parameters.AddWithValue("$item_id", itemId);
            insertCommand.Parameters.AddWithValue("$chunk_no", chunk.ChunkNo);
            insertCommand.Parameters.AddWithValue("$page_no", ToDbValue(chunk.PageNo));
            insertCommand.Parameters.AddWithValue("$sheet_name", ToDbValue(chunk.SheetName));
            insertCommand.Parameters.AddWithValue("$content_text", chunk.Text);
            insertCommand.Parameters.AddWithValue("$normalized_content", normalizedContent);
            insertCommand.Parameters.AddWithValue("$created_at", ToSql(DateTimeOffset.UtcNow));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = (SqliteTransaction)transaction;
            updateCommand.CommandText = """
                UPDATE items
                SET content_index_status = $status
                WHERE id = $item_id;
                """;
            updateCommand.Parameters.AddWithValue("$item_id", itemId);
            updateCommand.Parameters.AddWithValue("$status", chunks.Count == 0 ? "indexed_empty" : "indexed");
            await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkContentIndexFailedAsync(
        long itemId,
        string errorType,
        string message,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = "DELETE FROM content_index WHERE item_id = $item_id;";
            deleteCommand.Parameters.AddWithValue("$item_id", itemId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteErrorCommand = connection.CreateCommand())
        {
            deleteErrorCommand.Transaction = (SqliteTransaction)transaction;
            deleteErrorCommand.CommandText = "DELETE FROM extraction_errors WHERE item_id = $item_id;";
            deleteErrorCommand.Parameters.AddWithValue("$item_id", itemId);
            await deleteErrorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = (SqliteTransaction)transaction;
            updateCommand.CommandText = """
                UPDATE items
                SET content_index_status = 'failed'
                WHERE id = $item_id;
                """;
            updateCommand.Parameters.AddWithValue("$item_id", itemId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var errorCommand = connection.CreateCommand())
        {
            errorCommand.Transaction = (SqliteTransaction)transaction;
            errorCommand.CommandText = """
                INSERT INTO extraction_errors (item_id, error_type, error_message, last_attempt_at, retry_count)
                VALUES ($item_id, $error_type, $error_message, $last_attempt_at, 0);
                """;
            errorCommand.Parameters.AddWithValue("$item_id", itemId);
            errorCommand.Parameters.AddWithValue("$error_type", errorType);
            errorCommand.Parameters.AddWithValue("$error_message", message);
            errorCommand.Parameters.AddWithValue("$last_attempt_at", ToSql(DateTimeOffset.UtcNow));
            await errorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT
                id,
                root_id,
                full_path,
                parent_path,
                name,
                normalized_name,
                normalized_name_no_space,
                normalized_path,
                normalized_path_no_space,
                extension,
                is_directory,
                size,
                created_at,
                modified_at,
                last_seen_at,
                attributes,
                is_missing
            FROM items
            WHERE is_missing = 0
            """);

        sql.AppendLine();
        AppendTypeFilter(sql, command, request.TypeFilter);
        AppendScopeFilter(sql, command, request.ScopePath, request.IncludeSubfolders);
        AppendQueryPrefilter(sql, command, request);

        sql.AppendLine();
        if (request.Query is EmptyNode)
        {
            // Browse mode: SQL provides both ordering and a hard cap. For real
            // queries the order is recomputed by relevance in C#, so ordering in
            // SQL here would only add a throwaway temp-B-tree sort over every
            // candidate row.
            sql.AppendLine("ORDER BY modified_at DESC, name COLLATE NOCASE ASC");
            sql.AppendLine("LIMIT $limit");
            command.Parameters.AddWithValue("$limit", Math.Max(1, request.Limit));
        }

        command.CommandText = sql.ToString();

        var evaluator = request.Query is EmptyNode
            ? null
            : new QueryEvaluator(request.IgnoreWhitespace, request.Query);
        var useContent = QueryNeedsContent(request.Query, request.IncludeContent);
        if (request.Query is EmptyNode)
        {
            var emptyResults = new List<SearchResult>();
            await using var emptyReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await emptyReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                emptyResults.Add(new SearchResult
                {
                    Item = ReadItem(emptyReader),
                    MatchKind = SearchMatchKind.Name,
                    Score = 10
                });
            }

            return emptyResults.ToArray();
        }

        var candidates = new List<IndexedItem>();
        await using (var candidateReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await candidateReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(ReadItem(candidateReader));
            }
        }

        var contentMap = useContent
            ? await ReadContentMapAsync(connection, candidates.Select(item => item.Id).ToArray(), cancellationToken).ConfigureAwait(false)
            : new Dictionary<long, SearchContent>();
        var results = new List<SearchResult>();
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            contentMap.TryGetValue(item.Id, out var content);
            var evaluation = evaluator!.Evaluate(item, request.Query, content?.NormalizedText);
            if (!evaluation.IsMatch)
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Item = item,
                MatchKind = evaluation.MatchKind,
                Score = evaluation.Score
            });
        }

        return results
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Item.ModifiedAt)
            .ThenBy(result => result.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, request.Limit))
            .ToArray();
    }

    private static async Task<Dictionary<long, SearchContent>> ReadContentMapAsync(
        SqliteConnection connection,
        IReadOnlyList<long> itemIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, SearchContent>();
        if (itemIds.Count == 0)
        {
            return map;
        }

        foreach (var chunk in itemIds.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            var parameters = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var parameterName = "$item_id_" + i.ToString(CultureInfo.InvariantCulture);
                parameters.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, chunk[i]);
            }

            command.CommandText = $$"""
                SELECT item_id,
                       normalized_content
                FROM content_index
                WHERE item_id IN ({{string.Join(", ", parameters)}})
                ORDER BY item_id, chunk_no;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = reader.GetInt64(0);
                var existing = map.TryGetValue(itemId, out var current)
                    ? current
                    : new SearchContent(string.Empty);
                map[itemId] = new SearchContent(AppendContent(existing.NormalizedText, reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }

        return map;
    }

    private static string AppendContent(string existing, string next)
    {
        if (string.IsNullOrEmpty(existing))
        {
            return next;
        }

        if (string.IsNullOrEmpty(next))
        {
            return existing;
        }

        return existing + Environment.NewLine + next;
    }

    private static bool QueryNeedsContent(QueryNode node, bool includeContent)
    {
        return node switch
        {
            MatchNode { Field: SearchField.Content } => true,
            MatchNode { Field: SearchField.Any } => includeContent,
            AndNode andNode => andNode.Children.Any(child => QueryNeedsContent(child, includeContent)),
            OrNode orNode => orNode.Children.Any(child => QueryNeedsContent(child, includeContent)),
            NotNode notNode => QueryNeedsContent(notNode.Child, includeContent),
            _ => false
        };
    }

    public async Task<int> CountItemsAsync(
        string? scopePath = null,
        bool includeSubfolders = true,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder("SELECT COUNT(*) FROM items WHERE is_missing = 0");
        AppendScopeFilter(sql, command, scopePath, includeSubfolders);
        command.CommandText = sql.ToString();
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private SqliteConnection OpenConnection()
    {
        // Private cache (the default) is intentional: shared cache forces
        // process-wide table locks that serialize readers and interact poorly
        // with WAL. Each operation here uses its own short-lived connection,
        // so private cache + pooling is both faster and safer.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30
        };

        return new SqliteConnection(builder.ToString());
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value)? parameter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (parameter.HasValue)
        {
            command.Parameters.AddWithValue(parameter.Value.Name, parameter.Value.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryEnsureFtsTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                CREATE VIRTUAL TABLE IF NOT EXISTS item_fts USING fts5(
                    item_id UNINDEXED,
                    name,
                    name_no_space,
                    path,
                    path_no_space,
                    tokenize = 'trigram'
                );
                """,
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task BackfillItemFtsIfStaleAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Keep item_fts mirroring items(is_missing = 0). This covers databases
        // created before the FTS index existed (or otherwise out of sync) so the
        // FTS-only search prefilter can never silently miss rows. It is a one-time
        // O(N) rebuild only when the row counts differ; the steady state is two
        // cheap COUNT(*) reads and an early return.
        var itemCount = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM items WHERE is_missing = 0;",
            cancellationToken).ConfigureAwait(false);
        var ftsCount = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM item_fts;",
            cancellationToken).ConfigureAwait(false);
        if (itemCount == ftsCount)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, (SqliteTransaction)transaction, "DELETE FROM item_fts;", null, cancellationToken).ConfigureAwait(false);

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = (SqliteTransaction)transaction;
            insertCommand.CommandText = """
                INSERT INTO item_fts(rowid, item_id, name, name_no_space, path, path_no_space)
                SELECT id,
                       id,
                       normalized_name,
                       normalized_name_no_space,
                       normalized_path,
                       normalized_path_no_space
                FROM items
                WHERE is_missing = 0;
                """;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task TryDropLegacyContentFtsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "DROP TABLE IF EXISTS content_fts;",
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Old builds created this optional virtual table. If SQLite cannot
            // load the legacy module, content_index cleanup should still finish.
        }
    }

    private static async Task RebuildItemFtsForRootAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "DELETE FROM item_fts WHERE item_id IN (SELECT id FROM items WHERE root_id = $root_id);",
            ("$root_id", rootId),
            cancellationToken).ConfigureAwait(false);

        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO item_fts(rowid, item_id, name, name_no_space, path, path_no_space)
            SELECT id,
                   id,
                   normalized_name,
                   normalized_name_no_space,
                   normalized_path,
                   normalized_path_no_space
            FROM items
            WHERE root_id = $root_id
              AND is_missing = 0;
            """;
        insertCommand.Parameters.AddWithValue("$root_id", rootId);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IndexRoot> GetRootByPathAsync(
        SqliteConnection connection,
        string path,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, path, is_enabled, created_at, updated_at, last_indexed_at
            FROM roots
            WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);
        return await ReadSingleRootAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRootDoesNotOverlapAsync(
        SqliteConnection connection,
        string path,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path
            FROM roots
            WHERE is_enabled = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var existingPath = NormalizeRootPath(reader.GetString(0));
            if (string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsSamePathOrChild(path, existingPath))
            {
                throw new InvalidOperationException($"이미 상위 검색 위치가 등록되어 있습니다: {existingPath}");
            }

            if (IsSamePathOrChild(existingPath, path))
            {
                throw new InvalidOperationException($"이미 하위 검색 위치가 등록되어 있습니다: {existingPath}");
            }
        }
    }

    private static async Task<IndexRoot> GetRootByIdAsync(
        SqliteConnection connection,
        long rootId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, path, is_enabled, created_at, updated_at, last_indexed_at
            FROM roots
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", rootId);
        return await ReadSingleRootAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IndexRoot> ReadSingleRootAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DataException("검색 루트 정보를 찾을 수 없습니다.");
        }

        return ReadRoot(reader);
    }

    private static void AppendTypeFilter(StringBuilder sql, SqliteCommand command, ItemTypeFilter typeFilter)
    {
        switch (typeFilter)
        {
            case ItemTypeFilter.FileOnly:
                sql.AppendLine("AND is_directory = 0");
                break;
            case ItemTypeFilter.FolderOnly:
                sql.AppendLine("AND is_directory = 1");
                break;
            case ItemTypeFilter.All:
            default:
                break;
        }
    }

    private static void AppendScopeFilter(
        StringBuilder sql,
        SqliteCommand command,
        string? scopePath,
        bool includeSubfolders)
    {
        var normalizedScopePath = NormalizeScopePath(scopePath);
        if (normalizedScopePath is null)
        {
            return;
        }

        sql.AppendLine();
        if (includeSubfolders)
        {
            sql.AppendLine("AND (full_path = $scope_path COLLATE NOCASE OR full_path LIKE $scope_prefix ESCAPE '\\')");
            command.Parameters.AddWithValue("$scope_path", normalizedScopePath);
            command.Parameters.AddWithValue("$scope_prefix", EscapeLikePattern(EnsureTrailingSeparator(normalizedScopePath)) + "%");
        }
        else
        {
            sql.AppendLine("AND (full_path = $scope_path COLLATE NOCASE OR parent_path = $scope_path COLLATE NOCASE)");
            command.Parameters.AddWithValue("$scope_path", normalizedScopePath);
        }
    }

    private void AppendQueryPrefilter(StringBuilder sql, SqliteCommand command, SearchRequest request)
    {
        if (TryBuildQueryPrefilter(request.Query, request.IgnoreWhitespace, QueryNeedsContent(request.Query, request.IncludeContent), _ftsAvailable, command, out var filter))
        {
            sql.AppendLine("AND " + filter);
        }
    }

    private static bool TryBuildQueryPrefilter(
        QueryNode node,
        bool ignoreWhitespace,
        bool queryCanUseContent,
        bool useFts,
        SqliteCommand command,
        out string filter)
    {
        filter = string.Empty;
        switch (node)
        {
            case MatchNode match:
                return TryBuildMatchPrefilter(match, ignoreWhitespace, queryCanUseContent, useFts, command, out filter);
            case AndNode andNode:
                var andFilters = new List<string>();
                foreach (var child in andNode.Children)
                {
                    if (TryBuildQueryPrefilter(child, ignoreWhitespace, queryCanUseContent, useFts, command, out var childFilter))
                    {
                        andFilters.Add(childFilter);
                    }
                }

                if (andFilters.Count == 0)
                {
                    return false;
                }

                filter = "(" + string.Join(" AND ", andFilters) + ")";
                return true;
            case OrNode orNode:
                var orFilters = new List<string>();
                foreach (var child in orNode.Children)
                {
                    if (!TryBuildQueryPrefilter(child, ignoreWhitespace, queryCanUseContent, useFts, command, out var childFilter))
                    {
                        return false;
                    }

                    orFilters.Add(childFilter);
                }

                filter = "(" + string.Join(" OR ", orFilters) + ")";
                return orFilters.Count > 0;
            default:
                return false;
        }
    }

    private static bool TryBuildMatchPrefilter(
        MatchNode match,
        bool ignoreWhitespace,
        bool queryCanUseContent,
        bool useFts,
        SqliteCommand command,
        out string filter)
    {
        filter = string.Empty;
        if (match.Value.Length == 0 || match.IsRegex || match.IsWildcard)
        {
            return false;
        }

        return match.Field switch
        {
            SearchField.Any => queryCanUseContent
                ? TryBuildAnyWithContentPrefilter(command, match.Value, ignoreWhitespace, useFts, out filter)
                : TryBuildItemTextPrefilter(
                    command,
                    match.Value,
                    ignoreWhitespace,
                    useFts,
                    ["normalized_name", "normalized_path"],
                    ["normalized_name_no_space", "normalized_path_no_space"],
                    ["name", "path"],
                    ["name_no_space", "path_no_space"],
                    out filter),
            SearchField.Name => TryBuildItemTextPrefilter(
                command,
                match.Value,
                ignoreWhitespace,
                useFts,
                ["normalized_name"],
                ["normalized_name_no_space"],
                ["name"],
                ["name_no_space"],
                out filter),
            SearchField.Path => TryBuildItemTextPrefilter(
                command,
                match.Value,
                ignoreWhitespace,
                useFts,
                ["normalized_path"],
                ["normalized_path_no_space"],
                ["path"],
                ["path_no_space"],
                out filter),
            SearchField.Extension => TryBuildExtensionPrefilter(command, match.Value, out filter),
            SearchField.Type => TryBuildTypePrefilter(match.Value, out filter),
            SearchField.Content => TryBuildContentPrefilter(command, match.Value, ignoreWhitespace, out filter),
            _ => false
        };
    }

    private static bool TryBuildAnyWithContentPrefilter(
        SqliteCommand command,
        string value,
        bool ignoreWhitespace,
        bool useFts,
        out string filter)
    {
        var filters = new List<string>();
        if (TryBuildItemTextPrefilter(
                command,
                value,
                ignoreWhitespace,
                useFts,
                ["normalized_name", "normalized_path"],
                ["normalized_name_no_space", "normalized_path_no_space"],
                ["name", "path"],
                ["name_no_space", "path_no_space"],
                out var metadataFilter))
        {
            filters.Add(metadataFilter);
        }

        if (TryBuildContentPrefilter(command, value, ignoreWhitespace, out var contentFilter))
        {
            filters.Add(contentFilter);
        }

        filter = "(" + string.Join(" OR ", filters) + ")";
        return filters.Count > 0;
    }

    private static bool TryBuildContentPrefilter(
        SqliteCommand command,
        string value,
        bool ignoreWhitespace,
        out string filter)
    {
        filter = string.Empty;
        var conditions = new List<string>();
        var valueParameter = AddLikeParameter(command, value);
        conditions.Add($"ci.normalized_content LIKE {valueParameter} ESCAPE '\\'");

        var noSpaceValue = TextNormalizer.RemoveWhitespace(value);
        if (ignoreWhitespace && noSpaceValue.Length > 0)
        {
            var noSpaceParameter = AddLikeParameter(command, noSpaceValue);
            conditions.Add($"REPLACE(REPLACE(REPLACE(REPLACE(ci.normalized_content, ' ', ''), char(9), ''), char(10), ''), char(13), '') LIKE {noSpaceParameter} ESCAPE '\\'");
        }

        filter = "EXISTS (SELECT 1 FROM content_index ci WHERE ci.item_id = items.id AND (" + string.Join(" OR ", conditions) + "))";
        return conditions.Count > 0;
    }

    private static bool TryBuildItemTextPrefilter(
        SqliteCommand command,
        string value,
        bool ignoreWhitespace,
        bool useFts,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> noSpaceColumns,
        IReadOnlyList<string> ftsColumns,
        IReadOnlyList<string> ftsNoSpaceColumns,
        out string filter)
    {
        // For terms of 3+ characters the trigram FTS index is an exact superset
        // of a LIKE '%term%' substring scan, so it is used on its own. The former
        // "FTS OR (NOT EXISTS in fts AND LIKE)" fallback turned every search into
        // an O(N^2) correlated scan of the (UNINDEXED) FTS table — the root cause
        // of the search hang. FTS completeness is guaranteed by the backfill in
        // InitializeAsync and by RebuildItemFtsForRootAsync on every re-index.
        if (useFts && CanUseFts(value) &&
            TryBuildFtsPrefilter(command, "item_fts", "item_id", value, ignoreWhitespace, ftsColumns, ftsNoSpaceColumns, out filter))
        {
            return true;
        }

        return TryBuildLikeTextPrefilter(command, value, ignoreWhitespace, columns, noSpaceColumns, out filter);
    }

    private static bool TryBuildLikeTextPrefilter(
        SqliteCommand command,
        string value,
        bool ignoreWhitespace,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> noSpaceColumns,
        out string filter)
    {
        var conditions = new List<string>();
        var valueParameter = AddLikeParameter(command, value);
        conditions.AddRange(columns.Select(column => $"{column} LIKE {valueParameter} ESCAPE '\\'"));

        var noSpaceValue = TextNormalizer.RemoveWhitespace(value);
        if (ignoreWhitespace && noSpaceValue.Length > 0)
        {
            var noSpaceParameter = AddLikeParameter(command, noSpaceValue);
            conditions.AddRange(noSpaceColumns.Select(column => $"{column} LIKE {noSpaceParameter} ESCAPE '\\'"));
        }

        filter = "(" + string.Join(" OR ", conditions) + ")";
        return conditions.Count > 0;
    }

    private static bool TryBuildFtsPrefilter(
        SqliteCommand command,
        string tableName,
        string itemIdColumn,
        string value,
        bool ignoreWhitespace,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> noSpaceColumns,
        out string filter)
    {
        var ftsQuery = BuildFtsQuery(columns, value);
        var noSpaceValue = TextNormalizer.RemoveWhitespace(value);
        if (ignoreWhitespace && CanUseFts(noSpaceValue))
        {
            ftsQuery = "(" + ftsQuery + ") OR (" + BuildFtsQuery(noSpaceColumns, noSpaceValue) + ")";
        }

        var parameter = AddFtsParameter(command, ftsQuery);
        filter = $"items.id IN (SELECT {itemIdColumn} FROM {tableName} WHERE {tableName} MATCH {parameter})";
        return true;
    }

    private static string BuildFtsQuery(IReadOnlyList<string> columns, string value)
    {
        var phrase = QuoteFtsPhrase(value);
        return columns.Count == 1
            ? $"{columns[0]}:{phrase}"
            : string.Join(" OR ", columns.Select(column => $"{column}:{phrase}"));
    }

    private static string QuoteFtsPhrase(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static bool CanUseFts(string value)
    {
        return TextNormalizer.RemoveWhitespace(value).Length >= 3;
    }

    private static bool TryBuildExtensionPrefilter(SqliteCommand command, string value, out string filter)
    {
        var parameter = "$q" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue(parameter, value.TrimStart('.').ToLowerInvariant());
        filter = "(extension = " + parameter + " COLLATE NOCASE)";
        return true;
    }

    private static bool TryBuildTypePrefilter(string value, out string filter)
    {
        filter = value switch
        {
            "file" => "(is_directory = 0)",
            "folder" or "directory" => "(is_directory = 1)",
            _ => string.Empty
        };

        return filter.Length > 0;
    }

    private static string AddLikeParameter(SqliteCommand command, string value)
    {
        var parameter = "$q" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue(parameter, "%" + EscapeLikePattern(value) + "%");
        return parameter;
    }

    private static string AddFtsParameter(SqliteCommand command, string value)
    {
        var parameter = "$q" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue(parameter, value);
        return parameter;
    }

    private static void AddItemParameters(SqliteCommand command, IndexedItem item)
    {
        command.Parameters.AddWithValue("$root_id", item.RootId);
        command.Parameters.AddWithValue("$full_path", item.FullPath);
        command.Parameters.AddWithValue("$parent_path", item.ParentPath);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$normalized_name", item.NormalizedName);
        command.Parameters.AddWithValue("$normalized_name_no_space", item.NormalizedNameNoSpace);
        command.Parameters.AddWithValue("$normalized_path", item.NormalizedPath);
        command.Parameters.AddWithValue("$normalized_path_no_space", item.NormalizedPathNoSpace);
        command.Parameters.AddWithValue("$extension", ToDbValue(item.Extension));
        command.Parameters.AddWithValue("$is_directory", item.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("$size", ToDbValue(item.Size));
        command.Parameters.AddWithValue("$created_at", ToDbValue(item.CreatedAt));
        command.Parameters.AddWithValue("$modified_at", ToDbValue(item.ModifiedAt));
        command.Parameters.AddWithValue("$last_seen_at", ToSql(item.LastSeenAt));
        command.Parameters.AddWithValue("$attributes", item.Attributes);
    }

    private static IndexRoot ReadRoot(SqliteDataReader reader)
    {
        return new IndexRoot
        {
            Id = reader.GetInt64(0),
            Path = reader.GetString(1),
            IsEnabled = reader.GetInt32(2) == 1,
            CreatedAt = FromSql(reader.GetString(3)),
            UpdatedAt = FromSql(reader.GetString(4)),
            LastIndexedAt = reader.IsDBNull(5) ? null : FromSql(reader.GetString(5))
        };
    }

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static IndexedItem ReadItem(SqliteDataReader reader)
    {
        return new IndexedItem
        {
            Id = reader.GetInt64(0),
            RootId = reader.GetInt64(1),
            FullPath = reader.GetString(2),
            ParentPath = reader.GetString(3),
            Name = reader.GetString(4),
            NormalizedName = reader.GetString(5),
            NormalizedNameNoSpace = reader.GetString(6),
            NormalizedPath = reader.GetString(7),
            NormalizedPathNoSpace = reader.GetString(8),
            Extension = reader.IsDBNull(9) ? null : reader.GetString(9),
            IsDirectory = reader.GetInt32(10) == 1,
            Size = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            CreatedAt = reader.IsDBNull(12) ? null : FromSql(reader.GetString(12)),
            ModifiedAt = reader.IsDBNull(13) ? null : FromSql(reader.GetString(13)),
            LastSeenAt = FromSql(reader.GetString(14)),
            Attributes = reader.GetString(15),
            IsMissing = reader.GetInt32(16) == 1
        };
    }

    private static string? NormalizeScopePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception) when (
            path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            path.Length == 0)
        {
            return path;
        }
    }

    private static string NormalizeRootPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsInScope(IndexedItem item, string? scopePath, bool includeSubfolders)
    {
        if (string.IsNullOrWhiteSpace(scopePath))
        {
            return true;
        }

        var fullPath = NormalizeScopePath(item.FullPath) ?? item.FullPath;
        var parentPath = NormalizeScopePath(item.ParentPath) ?? item.ParentPath;
        if (string.Equals(fullPath, scopePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parentPath, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!includeSubfolders)
        {
            return false;
        }

        var scopePrefix = EnsureTrailingSeparator(scopePath);
        return fullPath.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePathOrChild(string path, string rootPath)
    {
        return string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(EnsureTrailingSeparator(rootPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    private static object ToDbValue(string? value)
    {
        return string.IsNullOrEmpty(value) ? DBNull.Value : value;
    }

    private static object ToDbValue(long? value)
    {
        return value.HasValue ? value.Value : DBNull.Value;
    }

    private static object ToDbValue(int? value)
    {
        return value.HasValue ? value.Value : DBNull.Value;
    }

    private static object ToDbValue(DateTimeOffset? value)
    {
        return value.HasValue ? ToSql(value.Value) : DBNull.Value;
    }

    private static string ToSql(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset FromSql(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static readonly string[] SchemaStatements =
    [
        "PRAGMA journal_mode = WAL;",
        "PRAGMA synchronous = NORMAL;",
        "PRAGMA foreign_keys = ON;",
        """
        CREATE TABLE IF NOT EXISTS roots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT NOT NULL UNIQUE,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            include_subfolders INTEGER NOT NULL DEFAULT 1,
            include_hidden INTEGER NOT NULL DEFAULT 0,
            include_system INTEGER NOT NULL DEFAULT 0,
            enable_content_index INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            last_indexed_at TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            root_id INTEGER NOT NULL,
            full_path TEXT NOT NULL UNIQUE,
            parent_path TEXT NOT NULL,
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL,
            normalized_name_no_space TEXT NOT NULL,
            normalized_path TEXT NOT NULL,
            normalized_path_no_space TEXT NOT NULL,
            extension TEXT,
            is_directory INTEGER NOT NULL,
            size INTEGER,
            created_at TEXT,
            modified_at TEXT,
            last_seen_at TEXT NOT NULL,
            attributes TEXT NOT NULL,
            content_index_status TEXT NOT NULL DEFAULT 'not_requested',
            hash_status TEXT NOT NULL DEFAULT 'not_requested',
            is_missing INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (root_id) REFERENCES roots(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS content_index (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id INTEGER NOT NULL,
            chunk_no INTEGER NOT NULL DEFAULT 0,
            page_no INTEGER,
            sheet_name TEXT,
            content_text TEXT NOT NULL,
            normalized_content TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (item_id) REFERENCES items(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS exclusions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            root_id INTEGER,
            path_pattern TEXT NOT NULL,
            reason TEXT,
            created_at TEXT NOT NULL,
            FOREIGN KEY (root_id) REFERENCES roots(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS extraction_errors (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id INTEGER NOT NULL,
            error_type TEXT NOT NULL,
            error_message TEXT,
            last_attempt_at TEXT NOT NULL,
            retry_count INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (item_id) REFERENCES items(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS duplicate_candidates (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            group_key TEXT NOT NULL,
            item_id INTEGER NOT NULL,
            size INTEGER NOT NULL,
            extension TEXT,
            hash TEXT,
            confidence TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (item_id) REFERENCES items(id) ON DELETE CASCADE
        );
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_items_cleanup_after_delete
        AFTER DELETE ON items
        BEGIN
            DELETE FROM content_index WHERE item_id = OLD.id;
            DELETE FROM extraction_errors WHERE item_id = OLD.id;
            DELETE FROM duplicate_candidates WHERE item_id = OLD.id;
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_items_content_cleanup_after_metadata_change
        AFTER UPDATE OF is_directory, size, modified_at ON items
        WHEN OLD.is_directory IS NOT NEW.is_directory
          OR OLD.size IS NOT NEW.size
          OR OLD.modified_at IS NOT NEW.modified_at
        BEGIN
            DELETE FROM content_index WHERE item_id = NEW.id;
            DELETE FROM extraction_errors WHERE item_id = NEW.id;
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_roots_cleanup_after_delete
        AFTER DELETE ON roots
        BEGIN
            DELETE FROM items WHERE root_id = OLD.id;
            DELETE FROM exclusions WHERE root_id = OLD.id;
        END;
        """,
        "CREATE INDEX IF NOT EXISTS idx_items_root_id ON items(root_id);",
        "CREATE INDEX IF NOT EXISTS idx_items_name ON items(normalized_name);",
        "CREATE INDEX IF NOT EXISTS idx_items_name_no_space ON items(normalized_name_no_space);",
        "CREATE INDEX IF NOT EXISTS idx_items_path ON items(normalized_path);",
        "CREATE INDEX IF NOT EXISTS idx_items_path_no_space ON items(normalized_path_no_space);",
        "CREATE INDEX IF NOT EXISTS idx_items_extension ON items(extension);",
        "CREATE INDEX IF NOT EXISTS idx_items_modified_at ON items(modified_at);",
        "CREATE INDEX IF NOT EXISTS idx_items_parent_path ON items(parent_path);",
        "CREATE INDEX IF NOT EXISTS idx_items_root_parent_directory ON items(root_id, parent_path, is_directory);",
        "CREATE INDEX IF NOT EXISTS idx_items_scope ON items(is_missing, parent_path, full_path);",
        "CREATE INDEX IF NOT EXISTS idx_content_index_item_id ON content_index(item_id);"
    ];

    private sealed record SearchContent(string NormalizedText);
}
