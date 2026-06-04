using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class DbSearchReaderIssueTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _db;
    private readonly DbWriter _writer;
    private readonly DbReader _reader;

    public DbSearchReaderIssueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_search_issue_test_{Guid.NewGuid():N}.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
        _writer.MarkGraphReady();
        _writer.MarkIssuesReady();
        _writer.MarkFoldReady();
        _reader = new DbReader(_db.Connection);
    }

    [Fact]
    public void Search_PunctuationHeavyPhraseRanksExactSubstringFirst_Issue2998()
    {
        InsertIndexedFile(
            "src/search-boost-newer.cs",
            "csharp",
            """
            try
            {
            }
            catch
            {
            }
            """,
            modified: new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile(
            "src/search-boost-exact.cs",
            "csharp",
            "public void Run() { try { Work(); } catch { } }",
            modified: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var results = _reader.Search("catch {", pathPatterns: ["src/search-boost-*.cs"], limit: 2);

        Assert.NotEmpty(results);
        Assert.Equal("src/search-boost-exact.cs", results[0].Path);
    }

    [Fact]
    public void Search_GuardFiltersRejectTooBroadCandidateCollectionBeforePagination_Issue3082()
    {
        for (var i = 0; i < 200; i++)
            InsertIndexedFile($"src/guard-budget-{i:0000}.cs", "csharp", "public void Run() { BudgetNeedle(); }");

        InsertIndexedFile(
            "src/guard-budget-9999.cs",
            "csharp",
            """
            public void Setup() { GuardMarker(); }
            public void Run() { BudgetNeedle(); }
            """);

        var ex = Assert.Throws<SearchGuardCandidateLimitException>(() => _reader.Search(
            "BudgetNeedle",
            pathPatterns: ["src/guard-budget-*.cs"],
            limit: 1,
            guardFilters: [new SearchGuardFilter(SearchGuardRole.Require, SearchGuardDirection.Before, "GuardMarker")],
            guardWindow: 1));

        Assert.Equal(200, ex.CandidateLimit);
        Assert.Contains("guarded search inspected the maximum", ex.Message);
    }

    [Fact]
    public void Search_GuardFiltersDoNotRejectWhenCandidateCountExactlyMatchesBudget_Issue3082()
    {
        for (var i = 0; i < 200; i++)
            InsertIndexedFile($"src/guard-budget-exact-{i:0000}.cs", "csharp", "public void Run() { ExactBudgetNeedle(); }");

        var results = _reader.Search(
            "ExactBudgetNeedle",
            pathPatterns: ["src/guard-budget-exact-*.cs"],
            limit: 1,
            guardFilters: [new SearchGuardFilter(SearchGuardRole.Require, SearchGuardDirection.Before, "MissingGuardMarker")],
            guardWindow: 1);

        Assert.Empty(results);
    }

    private void InsertIndexedFile(string path, string lang, string content, DateTime? modified = null)
    {
        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = lang,
            Size = normalized.Length,
            Lines = lines.Length,
            Modified = modified ?? new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = lines.Length,
            Content = normalized,
        }]);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _db.Dispose();
        TestProjectHelper.DeleteFile(_dbPath);
    }
}
