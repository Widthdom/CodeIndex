using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for SuggestionStore (local JSON storage with deduplication).
/// SuggestionStoreのテスト（ローカルJSON蓄積 + 重複排除）。
/// </summary>
public class SuggestionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SuggestionStore _store;

    public SuggestionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SuggestionStore(_tempDir);
    }

    // --- ComputeHash tests / ComputeHash テスト ---

    [Fact]
    public void ComputeHash_SameInput_ReturnsSameHash()
    {
        var hash1 = SuggestionStore.ComputeHash("symbol_extraction", "csharp", "Missing record support");
        var hash2 = SuggestionStore.ComputeHash("symbol_extraction", "csharp", "Missing record support");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_DifferentDescription_ReturnsDifferentHash()
    {
        var hash1 = SuggestionStore.ComputeHash("symbol_extraction", "csharp", "Missing record support");
        var hash2 = SuggestionStore.ComputeHash("symbol_extraction", "csharp", "Missing enum support");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_CaseInsensitiveDescription()
    {
        // "Missing X" and "missing x" should produce the same hash
        // 「Missing X」と「missing x」は同じハッシュを返すべき
        var hash1 = SuggestionStore.ComputeHash("symbol_extraction", "csharp", "Missing Record Support");
        var hash2 = SuggestionStore.ComputeHash("symbol_extraction", "csharp", "missing record support");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_TrimsWhitespace()
    {
        var hash1 = SuggestionStore.ComputeHash("other", null, "some description");
        var hash2 = SuggestionStore.ComputeHash("other", null, "  some description  ");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_NullLanguage_TreatedAsEmpty()
    {
        var hash1 = SuggestionStore.ComputeHash("other", null, "desc");
        var hash2 = SuggestionStore.ComputeHash("other", "", "desc");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_DifferentCategory_ReturnsDifferentHash()
    {
        var hash1 = SuggestionStore.ComputeHash("symbol_extraction", "csharp", "some issue");
        var hash2 = SuggestionStore.ComputeHash("crash_report", "csharp", "some issue");
        Assert.NotEqual(hash1, hash2);
    }

    // --- TryAdd tests / TryAdd テスト ---

    [Fact]
    public void TryAdd_NewSuggestion_ReturnsTrue()
    {
        var record = MakeRecord("symbol_extraction", "csharp", "Missing record support");
        Assert.True(_store.TryAdd(record));
    }

    [Fact]
    public void TryAdd_Duplicate_ReturnsFalse()
    {
        var record1 = MakeRecord("symbol_extraction", "csharp", "Missing record support");
        var record2 = MakeRecord("symbol_extraction", "csharp", "Missing record support");

        Assert.True(_store.TryAdd(record1));
        Assert.False(_store.TryAdd(record2));
    }

    [Fact]
    public void TryAdd_DifferentSuggestions_BothSucceed()
    {
        var record1 = MakeRecord("symbol_extraction", "csharp", "Missing record support");
        var record2 = MakeRecord("language_support", "kotlin", "Add Kotlin support");

        Assert.True(_store.TryAdd(record1));
        Assert.True(_store.TryAdd(record2));

        var all = _store.LoadAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void TryAdd_CreatesFile()
    {
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        Assert.False(File.Exists(filePath));

        _store.TryAdd(MakeRecord("other", null, "Test suggestion"));

        Assert.True(File.Exists(filePath));
    }

    // --- LoadAll tests / LoadAll テスト ---

    [Fact]
    public void LoadAll_NoFile_ReturnsEmptyList()
    {
        var all = _store.LoadAll();
        Assert.Empty(all);
    }

    [Fact]
    public void LoadAll_EmptyFile_ReturnsEmptyList()
    {
        File.WriteAllText(Path.Combine(_tempDir, "suggestions-codeindex.json"), "");
        var all = _store.LoadAll();
        Assert.Empty(all);
    }

    [Fact]
    public void LoadAll_CorruptJson_ReturnsEmptyList()
    {
        File.WriteAllText(Path.Combine(_tempDir, "suggestions-codeindex.json"), "{not valid json[[[");
        var all = _store.LoadAll();
        Assert.Empty(all);
    }

    [Fact]
    public void LoadAll_PreservesAllFields()
    {
        var record = new SuggestionRecord
        {
            Category = "crash_report",
            Language = "typescript",
            Description = "NullReferenceException during search",
            Context = "Searching for arrow functions",
            Hash = SuggestionStore.ComputeHash("crash_report", "typescript", "NullReferenceException during search"),
            CreatedAt = new DateTime(2026, 4, 12, 10, 0, 0, DateTimeKind.Utc),
            CreatedByAgent = "codex/5",
            SessionId = "session-123",
            ClientVersion = "1.2.3",
            McpClientName = "codex",
            McpClientVersion = "5",
            ToolInvocationContext = "MCP regression triage",
            SubmittedToGitHub = false,
            GitHubIssueUrl = null,
        };

        _store.TryAdd(record);
        var loaded = _store.LoadAll();

        Assert.Single(loaded);
        var r = loaded[0];
        Assert.Equal("crash_report", r.Category);
        Assert.Equal("typescript", r.Language);
        Assert.Equal("NullReferenceException during search", r.Description);
        Assert.Equal("Searching for arrow functions", r.Context);
        Assert.Equal(record.Hash, r.Hash);
        Assert.Equal("codex/5", r.CreatedByAgent);
        Assert.Equal("session-123", r.SessionId);
        Assert.Equal("1.2.3", r.ClientVersion);
        Assert.Equal("codex", r.McpClientName);
        Assert.Equal("5", r.McpClientVersion);
        Assert.Equal("MCP regression triage", r.ToolInvocationContext);
        Assert.False(r.SubmittedToGitHub);
        Assert.Null(r.GitHubIssueUrl);
    }

    [Fact]
    public void LoadAll_LegacyRecordsDefaultMissingAttribution()
    {
        File.WriteAllText(Path.Combine(_tempDir, "suggestions-codeindex.json"),
            """
            [
              {
                "category": "other",
                "description": "Legacy suggestion without attribution",
                "hash": "abc123",
                "created_at": "2026-04-12T10:00:00Z"
              }
            ]
            """);

        var loaded = _store.LoadAll();

        Assert.Single(loaded);
        Assert.Equal("unknown", loaded[0].CreatedByAgent);
        Assert.Equal("unknown", loaded[0].SessionId);
        Assert.Equal("unknown", loaded[0].ClientVersion);
        Assert.Null(loaded[0].McpClientName);
        Assert.Null(loaded[0].McpClientVersion);
    }

    // --- MarkSubmitted tests / MarkSubmitted テスト ---

    [Fact]
    public void MarkSubmitted_UpdatesRecord()
    {
        var record = MakeRecord("symbol_extraction", "csharp", "Missing record support");
        _store.TryAdd(record);

        _store.MarkSubmitted(record.Hash, "https://github.com/widthdom/CodeIndex/issues/99");

        var all = _store.LoadAll();
        Assert.Single(all);
        Assert.True(all[0].SubmittedToGitHub);
        Assert.Equal("https://github.com/widthdom/CodeIndex/issues/99", all[0].GitHubIssueUrl);
    }

    [Fact]
    public void MarkSubmitted_NonexistentHash_DoesNothing()
    {
        var record = MakeRecord("other", null, "Some suggestion");
        _store.TryAdd(record);

        _store.MarkSubmitted("nonexistent_hash", "https://example.com");

        var all = _store.LoadAll();
        Assert.Single(all);
        Assert.False(all[0].SubmittedToGitHub);
    }

    // --- Atomic write and corruption recovery tests / アトミック書き込みと破損復旧テスト ---

    [Fact]
    public void CorruptFile_IsPreservedAsBackup()
    {
        // Write a corrupt file, then load — should rename to .bak
        // 破損ファイルを書き込み、ロード — .bak にリネームされるべき
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var backupPath = filePath + ".bak";
        File.WriteAllText(filePath, "{corrupt json[[[");

        var all = _store.LoadAll();
        Assert.Empty(all);
        Assert.True(File.Exists(backupPath), "Corrupt file should be preserved as .bak");
        Assert.False(File.Exists(filePath), "Original corrupt file should be removed");
    }

    [Fact]
    public void AtomicWrite_SurvivesAddAfterCorruption()
    {
        // After corruption recovery, new suggestions should work normally
        // 破損復旧後、新しい提案が正常に動作するべき
        File.WriteAllText(Path.Combine(_tempDir, "suggestions-codeindex.json"), "not json");

        _store.LoadAll(); // triggers backup
        var record = MakeRecord("other", null, "Post-corruption suggestion");
        Assert.True(_store.TryAdd(record));

        var all = _store.LoadAll();
        Assert.Single(all);
        Assert.Equal("Post-corruption suggestion", all[0].Description);
    }

    [Fact]
    public void TryAdd_MoveFailure_DoesNotLeaveOrphanTmpFile()
    {
        // Force File.Move to fail by pre-creating the destination as a directory.
        // The write-to-temp succeeds, but the rename onto a directory throws and
        // the temp file must be cleaned up so `.cdidx/` does not accumulate orphans (#1574).
        // File.Move を失敗させるため、宛先パスをディレクトリとして事前作成する。
        // 一時ファイルへの書き込みは成功するが、ディレクトリに対する rename は失敗するため、
        // `.cdidx/` に孤児が蓄積しないよう一時ファイルがクリーンアップされる必要がある (#1574)。
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var tmpPath = filePath + ".tmp";
        Directory.CreateDirectory(filePath);

        var record = MakeRecord("other", null, "Move failure cleanup");
        var ex = Record.Exception(() => _store.TryAdd(record));

        Assert.NotNull(ex);
        Assert.False(File.Exists(tmpPath), $"Orphan .tmp file should be cleaned up after Move failure: {tmpPath}");
    }

    // --- Helpers / ヘルパー ---

    private static SuggestionRecord MakeRecord(string category, string? language, string description)
    {
        return new SuggestionRecord
        {
            Category = category,
            Language = language,
            Description = description,
            Hash = SuggestionStore.ComputeHash(category, language, description),
            CreatedAt = DateTime.UtcNow,
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup / ベストエフォートのクリーンアップ
        }
    }
}
