using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalSearch.Core.Content;
using LocalSearch.Core.Data;
using LocalSearch.Core.Indexing;
using LocalSearch.Core.Models;
using LocalSearch.Core.Search;
using LocalSearch.Core.Text;
using Microsoft.Data.Sqlite;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LocalSearch.Tests;

public sealed class ContentIndexingTests : IDisposable
{
    private readonly string _workspace;

    public ContentIndexingTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "LocalSearchContentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public async Task Content_Indexing_Enables_Content_Field_Search()
    {
        var root = Path.Combine(_workspace, "content-root");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "memo.txt"), "계약 금액은 삼천만 원입니다.");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var content = new ContentIndexingService(store, CompositeContentExtractor.CreateDefault());
        var search = new SearchService(store);

        var indexResult = await indexing.AddOrRefreshRootAsync(root);
        var contentResult = await content.IndexRootContentAsync(indexResult.Root.Id);
        var fieldResults = await search.SearchAsync("content:\"계약 금액\"");
        var metadataOnly = await search.SearchAsync("삼천만원", new SearchOptions { IgnoreWhitespace = true, IncludeContent = false });
        var contentEnabled = await search.SearchAsync("삼천만원", new SearchOptions { IgnoreWhitespace = true, IncludeContent = true });

        Assert.Equal(1, contentResult.CompletedCount);
        Assert.Single(fieldResults);
        Assert.Empty(metadataOnly);
        Assert.Single(contentEnabled);
        Assert.Equal(SearchMatchKind.Content, contentEnabled[0].MatchKind);
    }

    [Fact]
    public async Task Content_Search_Uses_Selected_Scope()
    {
        var root = Path.Combine(_workspace, "content-scope-root");
        var teamA = Path.Combine(root, "TeamA");
        var teamB = Path.Combine(root, "TeamB");
        Directory.CreateDirectory(teamA);
        Directory.CreateDirectory(teamB);
        await File.WriteAllTextAsync(Path.Combine(teamA, "memo-a.txt"), "공통검색어 A팀 문서입니다.");
        await File.WriteAllTextAsync(Path.Combine(teamB, "memo-b.txt"), "공통검색어 B팀 문서입니다.");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var content = new ContentIndexingService(store, CompositeContentExtractor.CreateDefault());
        var search = new SearchService(store);

        var indexResult = await indexing.AddOrRefreshRootAsync(root);
        await content.IndexRootContentAsync(indexResult.Root.Id);

        var scoped = await search.SearchAsync(
            "공통검색어",
            new SearchOptions
            {
                ScopePath = teamA,
                IncludeSubfolders = true,
                IncludeContent = true
            });

        Assert.Single(scoped);
        Assert.Equal(Path.Combine(teamA, "memo-a.txt"), scoped[0].Item.FullPath);
    }

    [Fact]
    public async Task Metadata_Rescan_Preserves_Existing_Content_Index_When_File_Is_Unchanged()
    {
        var root = Path.Combine(_workspace, "preserve-content-root");
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "memo.txt");
        await File.WriteAllTextAsync(filePath, "보존되어야 할 내용 검색어입니다.");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var content = new ContentIndexingService(store, CompositeContentExtractor.CreateDefault());
        var search = new SearchService(store);

        var indexResult = await indexing.AddOrRefreshRootAsync(root);
        await content.IndexRootContentAsync(indexResult.Root.Id);
        await indexing.AddOrRefreshRootAsync(root);

        var results = await search.SearchAsync("보존되어야", new SearchOptions { IncludeContent = true });

        Assert.Single(results);
        Assert.Equal(filePath, results[0].Item.FullPath);
    }

    [Fact]
    public async Task Content_Indexing_Skips_Already_Indexed_Unchanged_Files()
    {
        var root = Path.Combine(_workspace, "skip-indexed-content-root");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "memo.txt"), "처음 한 번만 추출될 문서입니다.");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var extractor = new CountingTextExtractor();
        var content = new ContentIndexingService(store, extractor);

        var indexResult = await indexing.AddOrRefreshRootAsync(root);
        var first = await content.IndexRootContentAsync(indexResult.Root.Id);
        await indexing.AddOrRefreshRootAsync(root);
        var second = await content.IndexRootContentAsync(indexResult.Root.Id);

        Assert.Equal(1, first.CompletedCount);
        Assert.Equal(0, second.CompletedCount);
        Assert.Equal(1, extractor.ExtractCount);
    }

    [Fact]
    public async Task Delete_Content_Indexes_Drops_Legacy_Content_Fts_Table()
    {
        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        await store.InitializeAsync();
        await ExecuteSqlAsync(
            databasePath,
            """
            CREATE VIRTUAL TABLE content_fts USING fts5(item_id UNINDEXED, content);
            INSERT INTO content_fts(item_id, content) VALUES (1, 'legacy preview text');
            """);

        await store.DeleteContentIndexesAsync();
        var tableCount = await ReadLongAsync(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'content_fts';");

        Assert.Equal(0, tableCount);
    }

    [Fact]
    public async Task Metadata_Rescan_Drops_Stale_Content_Index_When_File_Changes()
    {
        var root = Path.Combine(_workspace, "reset-content-root");
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "memo.txt");
        await File.WriteAllTextAsync(filePath, "오래된 내용 검색어입니다.");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var content = new ContentIndexingService(store, CompositeContentExtractor.CreateDefault());
        var search = new SearchService(store);

        var indexResult = await indexing.AddOrRefreshRootAsync(root);
        await content.IndexRootContentAsync(indexResult.Root.Id);
        await File.WriteAllTextAsync(filePath, "새로운 내용입니다. 파일 크기도 바뀝니다.");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(3));
        await indexing.AddOrRefreshRootAsync(root);

        var staleResults = await search.SearchAsync("오래된", new SearchOptions { IncludeContent = true });

        Assert.Empty(staleResults);
    }

    [Fact]
    public async Task Content_Indexing_Continues_After_Unexpected_Extractor_Exception()
    {
        var root = Path.Combine(_workspace, "resilient-content-root");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "bad.txt"), "bad");
        await File.WriteAllTextAsync(Path.Combine(root, "good.txt"), "계속 처리된 문서입니다.");

        var databasePath = Path.Combine(_workspace, "index.db");
        var store = new SqliteIndexStore(databasePath);
        var indexing = new IndexingService(store, new FolderScanner());
        var content = new ContentIndexingService(store, new ThrowingOnceExtractor());
        var search = new SearchService(store);

        var indexResult = await indexing.AddOrRefreshRootAsync(root);
        var summary = await content.IndexRootContentAsync(indexResult.Root.Id);
        var results = await search.SearchAsync("계속 처리", new SearchOptions { IncludeContent = true });

        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Single(results);
    }

    [Fact]
    public async Task Plain_Text_Extractor_Falls_Back_To_CP949()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var filePath = Path.Combine(_workspace, "cp949.txt");
        await File.WriteAllBytesAsync(filePath, Encoding.GetEncoding(949).GetBytes("한글 CP949 문서"));

        var extractor = new PlainTextExtractor();
        var chunks = await extractor.ExtractAsync(CreateItem(filePath, "txt"));

        Assert.Contains("한글 CP949", chunks[0].Text);
    }

    [Fact]
    public async Task Zip_Extractor_Reads_CP949_Entry_Names()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var zipPath = Path.Combine(_workspace, "archive.zip");
        await using (var stream = File.Create(zipPath))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.GetEncoding(949));
            var entry = archive.CreateEntry("한글파일.txt");
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync("sample"u8.ToArray());
        }

        var extractor = new ZipListingExtractor();
        var chunks = await extractor.ExtractAsync(CreateItem(zipPath, "zip"));

        Assert.Contains("한글파일.txt", chunks[0].Text);
    }

    [Fact]
    public async Task Hwpx_Extractor_Only_Reads_Body_Section_Xml()
    {
        var hwpxPath = Path.Combine(_workspace, "document.hwpx");
        await using (var stream = File.Create(hwpxPath))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            await WriteZipEntryAsync(archive, "Contents/section0.xml", "<root><p>본문 검색어</p></root>");
            await WriteZipEntryAsync(archive, "Contents/header.xml", "<root><p>머리말 검색어</p></root>");
            await WriteZipEntryAsync(archive, "META-INF/manifest.xml", "<root><p>메타 검색어</p></root>");
        }

        var extractor = new HwpxContentExtractor();
        var chunks = await extractor.ExtractAsync(CreateItem(hwpxPath, "hwpx"));
        var text = string.Join(Environment.NewLine, chunks.Select(chunk => chunk.Text));

        Assert.Contains("본문 검색어", text);
        Assert.DoesNotContain("머리말 검색어", text);
        Assert.DoesNotContain("메타 검색어", text);
    }

    [Fact]
    public async Task Docx_Extractor_Reads_Headers_And_Footers()
    {
        var docxPath = Path.Combine(_workspace, "document.docx");
        CreateDocxWithHeaderAndFooter(docxPath);

        var extractor = new OpenXmlDocumentExtractor();
        var chunks = await extractor.ExtractAsync(CreateItem(docxPath, "docx"));
        var text = string.Join(Environment.NewLine, chunks.Select(chunk => chunk.Text));

        Assert.Contains("본문 검색어", text);
        Assert.Contains("머리말 검색어", text);
        Assert.Contains("꼬리말 검색어", text);
    }

    private static async Task WriteZipEntryAsync(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(content);
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadLongAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static IndexedItem CreateItem(string path, string extension)
    {
        var normalizedName = TextNormalizer.Normalize(Path.GetFileName(path));
        var normalizedPath = TextNormalizer.Normalize(path);
        return new IndexedItem
        {
            FullPath = path,
            ParentPath = Path.GetDirectoryName(path) ?? string.Empty,
            Name = Path.GetFileName(path),
            NormalizedName = normalizedName,
            NormalizedNameNoSpace = TextNormalizer.RemoveWhitespace(normalizedName),
            NormalizedPath = normalizedPath,
            NormalizedPathNoSpace = TextNormalizer.RemoveWhitespace(normalizedPath),
            Extension = extension,
            IsDirectory = false,
            Size = new FileInfo(path).Length,
            LastSeenAt = DateTimeOffset.UtcNow,
            Attributes = FileAttributes.Normal.ToString()
        };
    }

    private static void CreateDocxWithHeaderAndFooter(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document(new W.Body(
            new W.Paragraph(new W.Run(new W.Text("본문 검색어")))));

        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new W.Header(new W.Paragraph(new W.Run(new W.Text("머리말 검색어"))));
        headerPart.Header.Save();

        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new W.Footer(new W.Paragraph(new W.Run(new W.Text("꼬리말 검색어"))));
        footerPart.Footer.Save();

        mainPart.Document.Body!.AppendChild(new W.SectionProperties(
            new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(headerPart) },
            new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(footerPart) }));
        mainPart.Document.Save();
    }

    private sealed class ThrowingOnceExtractor : IContentExtractor
    {
        public bool CanExtract(IndexedItem item)
        {
            return string.Equals(item.Extension, "txt", StringComparison.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
        {
            if (item.Name.Contains("bad", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("unexpected extractor failure");
            }

            return Task.FromResult<IReadOnlyList<ContentChunk>>(
            [
                new ContentChunk
                {
                    ChunkNo = 0,
                    Text = "계속 처리된 문서입니다."
                }
            ]);
        }
    }

    private sealed class CountingTextExtractor : IContentExtractor
    {
        public int ExtractCount { get; private set; }

        public bool CanExtract(IndexedItem item)
        {
            return string.Equals(item.Extension, "txt", StringComparison.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<ContentChunk>> ExtractAsync(IndexedItem item, CancellationToken cancellationToken = default)
        {
            ExtractCount++;
            return Task.FromResult<IReadOnlyList<ContentChunk>>(
            [
                new ContentChunk
                {
                    ChunkNo = 0,
                    Text = "처음 한 번만 추출될 문서입니다."
                }
            ]);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }
}
