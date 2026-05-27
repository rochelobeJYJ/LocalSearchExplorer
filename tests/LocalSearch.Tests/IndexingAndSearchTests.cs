using LocalSearch.Core.Data;
using LocalSearch.Core.Indexing;
using LocalSearch.Core.Search;
using Microsoft.Data.Sqlite;

namespace LocalSearch.Tests;

public sealed class IndexingAndSearchTests : IDisposable
{
    private readonly string _workspace;

    public IndexingAndSearchTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "LocalSearchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public async Task Indexing_Stores_Metadata_And_Searches_By_Name()
    {
        var root = Path.Combine(_workspace, "업무 자료");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "연차 신청서 2026.txt"), "sample");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var search = new SearchService(store);

        var result = await indexing.AddOrRefreshRootAsync(root);
        var searchResults = await search.SearchAsync("연차신청서", new SearchOptions { IgnoreWhitespace = true });

        Assert.True(result.IndexedCount >= 2);
        Assert.Contains(searchResults, item => item.Item.Name == "연차 신청서 2026.txt");
    }

    [Fact]
    public async Task Indexing_Populates_Fts_Metadata_Index()
    {
        var root = Path.Combine(_workspace, "fts-metadata-root");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "프로젝트계약서.txt"), "sample");
        await File.WriteAllTextAsync(Path.Combine(root, "무관자료.txt"), "sample");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var search = new SearchService(store);

        await indexing.AddOrRefreshRootAsync(root);
        var results = await search.SearchAsync("계약서");
        var ftsCount = await ReadScalarAsync(databasePath, "SELECT COUNT(*) FROM item_fts;");

        Assert.Single(results);
        Assert.Equal("프로젝트계약서.txt", results[0].Item.Name);
        Assert.True(ftsCount >= 3);
    }

    [Fact]
    public async Task Search_Falls_Back_To_Like_When_Metadata_Fts_Is_Not_Backfilled()
    {
        var root = Path.Combine(_workspace, "fts-fallback-root");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "빠른검색자료.txt"), "sample");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        await indexing.AddOrRefreshRootAsync(root);

        await ExecuteNonQueryAsync(databasePath, "DELETE FROM item_fts;");
        SqliteConnection.ClearAllPools();

        var newStore = new SqliteIndexStore(databasePath);
        await newStore.InitializeAsync();
        var search = new SearchService(newStore);
        var results = await search.SearchAsync("빠른검색자료");

        Assert.Single(results);
        Assert.Equal("빠른검색자료.txt", results[0].Item.Name);
    }

    [Fact]
    public async Task Search_Filters_By_Extension_And_Type()
    {
        var root = Path.Combine(_workspace, "검색 루트");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "계약 폴더"));
        await File.WriteAllTextAsync(Path.Combine(root, "계약서.pdf"), "sample");
        await File.WriteAllTextAsync(Path.Combine(root, "계약서.txt"), "sample");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var search = new SearchService(store);

        await indexing.AddOrRefreshRootAsync(root);
        var pdfResults = await search.SearchAsync("계약서 ext:pdf");
        var folderResults = await search.SearchAsync("type:folder 계약");

        Assert.Single(pdfResults);
        Assert.Equal("계약서.pdf", pdfResults[0].Item.Name);
        Assert.Contains(folderResults, item => item.Item.Name == "계약 폴더");
    }

    [Fact]
    public async Task Search_Evaluates_Boolean_Operators_And_Field_Filters()
    {
        var root = Path.Combine(_workspace, "고급 검색");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "계약서_최종.pdf"), "sample");
        await File.WriteAllTextAsync(Path.Combine(root, "계약서_초안.pdf"), "sample");
        await File.WriteAllTextAsync(Path.Combine(root, "견적서_최종.docx"), "sample");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var search = new SearchService(store);

        await indexing.AddOrRefreshRootAsync(root);

        var andNot = await search.SearchAsync("계약서 & !초안 & ext:pdf");
        var or = await search.SearchAsync("(계약서, 견적서) & name:최종");
        var regex = await search.SearchAsync("re:/^계약서_최종.*\\.pdf$/");

        Assert.Single(andNot);
        Assert.Equal("계약서_최종.pdf", andNot[0].Item.Name);
        Assert.Equal(2, or.Count);
        Assert.Single(regex);
    }

    [Fact]
    public async Task Search_Respects_Scope_Path_And_Subfolder_Option()
    {
        var root = Path.Combine(_workspace, "scope-root");
        var teamA = Path.Combine(root, "TeamA");
        var teamB = Path.Combine(root, "TeamB");
        var nested = Path.Combine(teamA, "Nested");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(teamB);
        await File.WriteAllTextAsync(Path.Combine(teamA, "plan.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(nested, "plan-nested.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(teamB, "plan.txt"), "alpha");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var search = new SearchService(store);

        await indexing.AddOrRefreshRootAsync(root);

        var all = await search.SearchAsync("plan");
        var scopedRecursive = await search.SearchAsync("plan", new SearchOptions { ScopePath = teamA, IncludeSubfolders = true });
        var scopedDirectOnly = await search.SearchAsync("plan", new SearchOptions { ScopePath = teamA, IncludeSubfolders = false });
        var scopedRecursiveCount = await store.CountItemsAsync(teamA, includeSubfolders: true);
        var scopedDirectOnlyCount = await store.CountItemsAsync(teamA, includeSubfolders: false);

        Assert.Equal(3, all.Count);
        Assert.Equal(2, scopedRecursive.Count);
        Assert.Single(scopedDirectOnly);
        Assert.Equal(Path.Combine(teamA, "plan.txt"), scopedDirectOnly[0].Item.FullPath);
        Assert.Equal(4, scopedRecursiveCount);
        Assert.Equal(3, scopedDirectOnlyCount);
    }

    [Fact]
    public async Task Store_Returns_Root_Statuses_And_Child_Folders_For_Location_Tree()
    {
        var root = Path.Combine(_workspace, "tree-root");
        var firstChild = Path.Combine(root, "A");
        var secondChild = Path.Combine(root, "B");
        Directory.CreateDirectory(firstChild);
        Directory.CreateDirectory(secondChild);
        await File.WriteAllTextAsync(Path.Combine(firstChild, "note.txt"), "sample");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());

        var result = await indexing.AddOrRefreshRootAsync(root);
        var statuses = await store.GetRootIndexStatusesAsync();
        var children = await store.GetChildFoldersAsync(result.Root.Id, root);

        var status = Assert.Single(statuses);
        Assert.Equal(result.Root.Id, status.Root.Id);
        Assert.True(status.ItemCount >= 4);
        Assert.Equal(2, children.Count);
        Assert.Contains(children, item => item.FullPath == firstChild);
        Assert.Contains(children, item => item.FullPath == secondChild);
    }

    [Fact]
    public async Task Indexing_Applies_Exclusion_Rules()
    {
        var root = Path.Combine(_workspace, "exclude-root");
        var included = Path.Combine(root, "included");
        var excluded = Path.Combine(root, "node_modules");
        Directory.CreateDirectory(included);
        Directory.CreateDirectory(excluded);
        await File.WriteAllTextAsync(Path.Combine(included, "keep.txt"), "sample");
        await File.WriteAllTextAsync(Path.Combine(excluded, "skip.txt"), "sample");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var search = new SearchService(store);

        await store.InitializeAsync();
        var rootRecord = await store.UpsertRootAsync(root);
        await store.AddExclusionAsync(rootRecord.Id, "node_modules", "test");
        await indexing.AddOrRefreshRootAsync(root);

        var keep = await search.SearchAsync("keep");
        var skip = await search.SearchAsync("skip");

        Assert.Single(keep);
        Assert.Empty(skip);
    }

    [Fact]
    public async Task Exclusion_Rules_Match_Path_Segments_Not_Substrings()
    {
        var root = Path.Combine(_workspace, "exclude-segment-root");
        var exactExcluded = Path.Combine(root, "bin");
        var substringShouldStay = Path.Combine(root, "binary");
        Directory.CreateDirectory(exactExcluded);
        Directory.CreateDirectory(substringShouldStay);
        await File.WriteAllTextAsync(Path.Combine(exactExcluded, "skip.txt"), "sample");
        await File.WriteAllTextAsync(Path.Combine(substringShouldStay, "keep.txt"), "sample");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var search = new SearchService(store);

        await store.InitializeAsync();
        var rootRecord = await store.UpsertRootAsync(root);
        await store.AddExclusionAsync(rootRecord.Id, "bin", "test");
        await indexing.AddOrRefreshRootAsync(root);

        var keep = await search.SearchAsync("keep");
        var skip = await search.SearchAsync("skip");

        Assert.Single(keep);
        Assert.Empty(skip);
    }

    [Fact]
    public async Task Indexing_Rejects_Nested_Search_Roots()
    {
        var root = Path.Combine(_workspace, "nested-root");
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());

        await indexing.AddOrRefreshRootAsync(root);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => indexing.AddOrRefreshRootAsync(child));

        Assert.Contains("상위 검색 위치", exception.Message);
    }

    [Fact]
    public async Task Indexing_Rejects_Parent_Search_Root_When_Child_Is_Already_Registered()
    {
        var root = Path.Combine(_workspace, "parent-root");
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());

        await indexing.AddOrRefreshRootAsync(child);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => indexing.AddOrRefreshRootAsync(root));

        Assert.Contains("하위 검색 위치", exception.Message);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    private static async Task<long> ReadScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task ExecuteNonQueryAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
