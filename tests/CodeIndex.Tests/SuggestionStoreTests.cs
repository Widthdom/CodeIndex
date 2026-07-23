using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for SuggestionStore (local JSON storage with deduplication).
/// SuggestionStoreのテスト（ローカルJSON蓄積 + 重複排除）。
/// </summary>
[Collection("Console sensitive")]
public class SuggestionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SuggestionStore _store;

    public SuggestionStoreTests()
    {
        _tempDir = TestProjectHelper.CreateTempProject("suggestion_store");
        _store = new SuggestionStore(_tempDir);
    }

    [Fact]
    public void ValidCategories_IncludeExhaustiveAuditFindingTypes_Issue4423()
    {
        Assert.Contains("security", SuggestionRecord.ValidCategories);
        Assert.Contains("performance", SuggestionRecord.ValidCategories);
        Assert.Contains("bug", SuggestionRecord.ValidCategories);
        Assert.Contains("cleanup", SuggestionRecord.ValidCategories);
        Assert.Contains("documentation", SuggestionRecord.ValidCategories);
        Assert.Contains("feature_request", SuggestionRecord.ValidCategories);
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
    public void ComputeHash_UsesExternallyVisibleScrubbedDescription()
    {
        var hash1 = SuggestionStore.ComputeHash("other", null, "Code sample `secret()` is missing");
        var hash2 = SuggestionStore.ComputeHash("other", null, "Code sample `otherSecret()` is missing");

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

    [Fact]
    public void LoadAll_OversizedStore_PreservesBackupAndReturnsEmpty()
    {
        var path = Path.Combine(_tempDir, "suggestions-codeindex.json");
        CreateSparseFile(path, SuggestionStore.MaxSuggestionStoreBytes + 1L);

        var records = _store.LoadAll();

        Assert.Empty(records);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LoadByStatus_OversizedStore_PreservesBackupAndReturnsEmpty()
    {
        var path = Path.Combine(_tempDir, "suggestions-codeindex.json");
        CreateSparseFile(path, SuggestionStore.MaxSuggestionStoreBytes + 1L);

        var records = _store.LoadByStatus(SuggestionStatus.Draft);

        Assert.Empty(records);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LoadAll_ExcessiveJsonDepth_PreservesBackupAndReturnsEmpty()
    {
        var path = Path.Combine(_tempDir, "suggestions-codeindex.json");
        File.WriteAllText(path, BuildDeepSuggestionStoreJson(SuggestionStore.MaxSuggestionStoreJsonDepth + 1));

        var records = _store.LoadAll();

        Assert.Empty(records);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LoadAll_MigratesLegacyHashToStableIdAndRevision_Issue4588()
    {
        const string description = "Legacy suggestion identity";
        var legacyHash = SuggestionStore.ComputeHash("bug", "csharp", description);
        var path = Path.Combine(_tempDir, "suggestions-codeindex.json");
        File.WriteAllText(
            path,
            $$"""
            [
              {
                "category": "bug",
                "language": "csharp",
                "description": "{{description}}",
                "hash": "{{legacyHash}}"
              }
            ]
            """);

        var record = Assert.Single(_store.LoadAll());

        Assert.Equal(legacyHash, record.Id);
        Assert.Equal(SuggestionStore.ComputeRevisionHash(record), record.RevisionHash);
        Assert.NotEqual(legacyHash, record.RevisionHash);
        Assert.Equal(legacyHash, record.Hash);
    }

    [Fact]
    public void ComputeRevisionHash_CoversEveryEditableField_Issue4588()
    {
        var record = MakeRecord("bug", "csharp", "Revision coverage");
        var baseline = SuggestionStore.ComputeRevisionHash(record);

        record.Category = "performance";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.Category = "bug";
        record.Language = "fsharp";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.Language = "csharp";
        record.Description = "changed description";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.Description = "Revision coverage";
        record.Context = "changed context";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.Context = null;
        record.SampledTitle = "changed title";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.SampledTitle = null;
        record.EvidencePaths = ["src/example.cs"];
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.EvidencePaths = null;
        record.Agent = "changed-agent";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.Agent = null;
        record.ToolInvocationContext = "changed invocation";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.ToolInvocationContext = null;
        record.SampledTags = ["changed-tag"];
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.SampledTags = null;
        record.Status = SuggestionStatus.WontFix;
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.Status = SuggestionStatus.Draft;
        record.PreviousStatus = SuggestionStatus.WontFix;
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.PreviousStatus = null;
        record.StatusChangedAt = new DateTime(2035, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.StatusChangedAt = null;
        record.StatusChangedBy = "maintainer";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
        record.StatusChangedBy = null;
        record.StatusChangeReason = "curated";
        Assert.NotEqual(baseline, SuggestionStore.ComputeRevisionHash(record));
    }

    private static void CreateSparseFile(string path, long length)
    {
        using var stream = File.Create(path);
        stream.SetLength(length);
    }

    [Fact]
    public void LoadAll_TooManyRecords_PreservesBackupAndReturnsEmpty()
    {
        var path = Path.Combine(_tempDir, "suggestions-codeindex.json");
        WriteEmptyRecordStore(path, SuggestionStore.MaxSuggestionStoreRecords + 1);

        var records = _store.LoadAll();

        Assert.Empty(records);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LoadByStatus_TooManyRecordsBeforeFilter_PreservesBackupAndReturnsEmpty()
    {
        var path = Path.Combine(_tempDir, "suggestions-codeindex.json");
        WriteEmptyRecordStore(path, SuggestionStore.MaxSuggestionStoreRecords + 1);

        var records = _store.LoadByStatus(SuggestionStatus.SubmittedPendingTriage);

        Assert.Empty(records);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LoadPage_TakeStopsBeforeLaterExcessRecords()
    {
        var path = Path.Combine(_tempDir, "suggestions-codeindex.json");
        WriteEmptyRecordStore(path, SuggestionStore.MaxSuggestionStoreRecords + 1);

        var records = _store.Load(skip: 0, take: 1);

        Assert.Single(records);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task ReadFilteredSnapshotAsync_ObservesCancellationDuringStreaming_Issue3908()
    {
        var records = new[]
        {
            MakeRecord("other", null, "first"),
            MakeRecord("other", null, "second"),
        };
        var snapshot = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(records));
        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SuggestionStore.ReadFilteredSnapshotAsync(
                snapshot,
                _ =>
                {
                    seen++;
                    cts.Cancel();
                    return true;
                },
                skip: 0,
                take: null,
                normalizeDefaults: false,
                cts.Token));

        Assert.Equal(1, seen);
    }

    // --- TryAdd tests / TryAdd テスト ---

    [Fact]
    public void TryAdd_NewSuggestion_ReturnsTrue()
    {
        var record = MakeRecord("symbol_extraction", "csharp", "Missing record support");
        Assert.True(_store.TryAdd(record));
    }

    [Fact]
    public void TryAdd_StampsCreatedAtFromInjectedClockWhenPersisted()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2030, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var record = MakeRecord("symbol_extraction", "csharp", "Missing record support");
        record.CreatedAt = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(store.TryAdd(record));

        var saved = Assert.Single(store.LoadAll());
        Assert.Equal(clock.GetUtcNow().UtcDateTime, saved.CreatedAt);
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
    public void TryAdd_FuzzyDuplicateSameCategoryAndLanguage_ReturnsFalse()
    {
        var record1 = MakeRecord("language_support", "javascript", "missing arrow function support");
        var record2 = MakeRecord("language_support", "javascript", "arrow functions not supported");

        Assert.True(_store.TryAdd(record1));
        Assert.False(_store.TryAdd(record2));

        var all = _store.LoadAll();
        Assert.Single(all);
        Assert.Equal(record1.Hash, all[0].Hash);
    }

    [Fact]
    public void TryAdd_FuzzyDuplicateDifferentCategory_BothSucceed()
    {
        var record1 = MakeRecord("language_support", "javascript", "missing arrow function support");
        var record2 = MakeRecord("reference_extraction", "javascript", "arrow functions not supported");

        Assert.True(_store.TryAdd(record1));
        Assert.True(_store.TryAdd(record2));

        Assert.Equal(2, _store.LoadAll().Count);
    }

    [Fact]
    public void TryAddAndSubmit_FuzzyDuplicate_ReturnsMatchedHashAndScore()
    {
        var record1 = MakeRecord("language_support", "javascript", "missing arrow function support");
        var record2 = MakeRecord("language_support", "javascript", "arrow functions not supported");

        var first = _store.TryAddAndSubmit(record1, null);
        var second = _store.TryAddAndSubmit(record2, null);

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(record1.Hash, second.DuplicateOfHash);
        Assert.True(second.DuplicateScore >= SuggestionStore.DefaultDedupThreshold);
    }

    [Fact]
    public void TryAddAndSubmit_UsesInjectedClockForRetryAndSubmissionTimestamps()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var record = MakeRecord("other", null, "submit me");

        var result = store.TryAddAndSubmit(record, _ => SuggestionStore.SubmitAttemptResult.Success("https://github.com/Widthdom/CodeIndex/issues/1"));

        Assert.True(result.IsNew);
        var saved = Assert.Single(store.LoadAll());
        Assert.Equal(clock.GetUtcNow().UtcDateTime, saved.CreatedAt);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, saved.LastSubmitAttempt);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, saved.LastSyncedAt);
    }

    [Fact]
    public async Task TryAddAndSubmitAsync_CanceledBeforeReservation_PropagatesWithoutPersisting()
    {
        var record = MakeRecord("other", null, "Canceled before reservation");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _store.TryAddAndSubmitAsync(
                record,
                (_, _) => Task.FromResult(SuggestionStore.SubmitAttemptResult.Success("https://github.com/widthdom/CodeIndex/issues/123")),
                cts.Token));

        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public void TryAddAndSubmit_CanceledBeforeReservation_PropagatesWithoutPersisting_Issue3658()
    {
        var record = MakeRecord("other", null, "Sync canceled before reservation");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _store.TryAddAndSubmit(
                record,
                _ => SuggestionStore.SubmitAttemptResult.Success("https://github.com/widthdom/CodeIndex/issues/123"),
                cts.Token));

        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public async Task TryAddAndSubmitAsync_PassesCancellationTokenToSubmitCallback()
    {
        var record = MakeRecord("other", null, "Submit observes cancellation token");
        using var cts = new CancellationTokenSource();
        var observedToken = CancellationToken.None;

        var result = await _store.TryAddAndSubmitAsync(
            record,
            (_, token) =>
            {
                observedToken = token;
                return Task.FromResult(SuggestionStore.SubmitAttemptResult.Success("https://github.com/widthdom/CodeIndex/issues/123"));
            },
            cts.Token);

        Assert.True(result.IsNew);
        Assert.Equal(cts.Token, observedToken);
    }

    [Fact]
    public void TryAddAndSubmit_FuzzyDuplicate_IgnoresClosedDiagnosticStderr()
    {
        var originalError = Console.Error;
        var closedError = new StringWriter();
        closedError.Dispose();

        lock (TestConsoleLock.Gate)
        {
            Console.SetError(closedError);
            try
            {
                var record1 = MakeRecord("language_support", "javascript", "missing arrow function support");
                var record2 = MakeRecord("language_support", "javascript", "arrow functions not supported");

                var first = _store.TryAddAndSubmit(record1, null);
                var second = _store.TryAddAndSubmit(record2, null);

                Assert.True(first.IsNew);
                Assert.False(second.IsNew);
                Assert.Equal(record1.Hash, second.DuplicateOfHash);
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
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

    [Fact]
    public void TryAdd_OnPosixCreatesPrivateStoreFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");

        Assert.True(_store.TryAdd(MakeRecord("other", null, "Private suggestion store")));

        AssertPrivateFileMode(filePath);
    }

    [Fact]
    public void TryAdd_OnPosixCreatesPrivateStoreDirectory_Issue3686()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.True(_store.TryAdd(MakeRecord("other", null, "Private suggestion directory")));

        Assert.Equal(
            DataDirectorySecurity.PrivateDirectoryMode,
            File.GetUnixFileMode(_tempDir) & DataDirectorySecurity.PermissionBits);
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
            Status = SuggestionStatus.Draft,
            UpstreamIssueNumber = null,
            UpstreamUrl = null,
            CreatedByAgent = "codex/5",
            SessionId = "session-123",
            ClientVersion = "1.2.3",
            McpClientName = "codex",
            McpClientVersion = "5",
            ToolInvocationContext = "MCP regression triage",
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
        Assert.Equal(SuggestionStatus.Draft, r.Status);
        Assert.Null(r.UpstreamIssueNumber);
        Assert.Null(r.UpstreamUrl);
        Assert.Equal("codex/5", r.CreatedByAgent);
        Assert.Equal("session-123", r.SessionId);
        Assert.Equal("1.2.3", r.ClientVersion);
        Assert.Equal("codex", r.McpClientName);
        Assert.Equal("5", r.McpClientVersion);
        Assert.Equal("MCP regression triage", r.ToolInvocationContext);
        Assert.Null(r.LastSubmitError);
        Assert.Null(r.LastSubmitAttempt);
        Assert.Equal(0, r.SubmitAttemptCount);
        Assert.Null(r.SubmittedToGitHub);
        Assert.Null(r.GitHubIssueUrl);
    }

    [Fact]
    public void TryAdd_RedactsSensitiveTextBeforePersistence()
    {
        var record = MakeRecord(
            "other",
            null,
            "AWS AKIA1234567890ABCDEF and password=swordfish and token=tok123 and github_token=git123 and api_key=abc123 and openai_api_key=oa123 and access-key=def456 and CDIDX_GITHUB_TOKEN=cdidx123 and Bearer AbCdEfGhIjKlMnOpQrStUvWxYz123456 should not persist");
        record.Context = "token aaBB11ccDD22eeFF33ggHH44iiJJ55kk";
        record.ToolInvocationContext = "secret=hunter2 access_key=ghi789";
        record.SampledTitle = "Sensitive text redaction";
        record.SampledTags = ["security", "suggestions"];
        record.EvidencePaths = ["src/CodeIndex/Cli/SuggestionStore.cs"];

        Assert.True(_store.TryAdd(record));

        var stored = Assert.Single(_store.LoadAll());
        Assert.Contains("[REDACTED:aws_access_key]", stored.Description);
        Assert.Contains("password=[REDACTED:credential]", stored.Description);
        Assert.Contains("token=[REDACTED:credential]", stored.Description);
        Assert.Contains("github_token=[REDACTED:credential]", stored.Description);
        Assert.Contains("api_key=[REDACTED:credential]", stored.Description);
        Assert.Contains("openai_api_key=[REDACTED:credential]", stored.Description);
        Assert.Contains("access-key=[REDACTED:credential]", stored.Description);
        Assert.Contains("CDIDX_GITHUB_TOKEN=[REDACTED:credential]", stored.Description);
        Assert.Contains("[REDACTED:bearer_token]", stored.Description);
        Assert.Contains("[REDACTED:high_entropy_token]", stored.Context);
        Assert.Contains("secret=[REDACTED:credential]", stored.ToolInvocationContext);
        Assert.Contains("access_key=[REDACTED:credential]", stored.ToolInvocationContext);
        Assert.Equal("Sensitive text redaction", stored.SampledTitle);
        Assert.Equal(["security", "suggestions"], stored.SampledTags);
        Assert.Equal(["src/CodeIndex/Cli/SuggestionStore.cs"], stored.EvidencePaths);
        Assert.DoesNotContain("AKIA1234567890ABCDEF", stored.Description);
        Assert.DoesNotContain("swordfish", stored.Description);
        Assert.DoesNotContain("tok123", stored.Description);
        Assert.DoesNotContain("git123", stored.Description);
        Assert.DoesNotContain("abc123", stored.Description);
        Assert.DoesNotContain("oa123", stored.Description);
        Assert.DoesNotContain("def456", stored.Description);
        Assert.DoesNotContain("cdidx123", stored.Description);
        Assert.DoesNotContain("hunter2", stored.ToolInvocationContext);
        Assert.DoesNotContain("ghi789", stored.ToolInvocationContext);
    }

    [Fact]
    public void TryAdd_RedactsSensitiveSampledMetadataBeforePersistence()
    {
        var record = MakeRecord("other", null, "Sampled metadata redaction");
        record.SampledTitle = "Sampled title api_key=sample-title-secret";
        record.SampledTags = ["security", "github_token=sample-tag-secret"];

        Assert.True(_store.TryAdd(record));

        var stored = Assert.Single(_store.LoadAll());
        Assert.Contains("api_key=[REDACTED:credential]", stored.SampledTitle!);
        Assert.Contains("github_token=[REDACTED:credential]", stored.SampledTags!);
        Assert.DoesNotContain("sample-title-secret", stored.SampledTitle!);
        Assert.DoesNotContain("sample-tag-secret", string.Join(" ", stored.SampledTags!));
    }

    [Fact]
    public void TryAdd_TruncatesLargeSensitiveFieldsBeforePersistence()
    {
        var tailSecret = "tail-secret-value-should-not-survive";
        var record = MakeRecord(
            "other",
            null,
            "api_key=" + new string('a', SuggestionStore.RedactionFieldLengthLimit) + tailSecret);

        Assert.True(_store.TryAdd(record));

        var stored = Assert.Single(_store.LoadAll());
        Assert.Contains("api_key=[REDACTED:credential]", stored.Description);
        Assert.Contains(SuggestionStore.RedactionTruncationMarker, stored.Description);
        Assert.DoesNotContain(tailSecret, stored.Description);
        Assert.True(stored.Description.Length < record.Description.Length);
    }

    [Fact]
    public void TryAddAndSubmit_Success_StampsAttemptStateAndClearsError()
    {
        var record = MakeRecord("other", null, "Submission succeeds");

        var result = _store.TryAddAndSubmit(record,
            _ => SuggestionStore.SubmitAttemptResult.Success("https://github.com/widthdom/CodeIndex/issues/123"));

        var stored = Assert.Single(_store.LoadAll());
        Assert.True(result.IsNew);
        Assert.Equal("https://github.com/widthdom/CodeIndex/issues/123", result.UpstreamUrl);
        Assert.Equal(SuggestionStatus.SubmittedPendingTriage, stored.Status);
        Assert.Equal(123, stored.UpstreamIssueNumber);
        Assert.Equal(1, stored.SubmitAttemptCount);
        Assert.NotNull(stored.LastSubmitAttempt);
        Assert.Null(stored.LastSubmitError);
    }

    [Fact]
    public void TryAddAndSubmit_Failure_StampsErrorWithoutSubmitting()
    {
        var record = MakeRecord("other", null, "Submission fails");

        var result = _store.TryAddAndSubmit(record,
            _ => SuggestionStore.SubmitAttemptResult.Failure("422: validation failed"));

        var stored = Assert.Single(_store.LoadAll());
        Assert.True(result.IsNew);
        Assert.Null(result.UpstreamUrl);
        Assert.Equal(SuggestionStatus.Draft, stored.Status);
        Assert.Equal(1, stored.SubmitAttemptCount);
        Assert.NotNull(stored.LastSubmitAttempt);
        Assert.Equal("422: validation failed", stored.LastSubmitError);
    }

    [Fact]
    public void TryAddAndSubmit_Failure_RedactsAndBoundsPersistedSubmitError()
    {
        var record = MakeRecord("other", null, "Submission stores redacted error");
        var tailSecret = "tail-secret-should-not-persist";
        var error = "api_key=" + new string('a', SuggestionStore.RedactionFieldLengthLimit) + tailSecret;

        var result = _store.TryAddAndSubmit(record,
            _ => SuggestionStore.SubmitAttemptResult.Failure(error));

        var stored = Assert.Single(_store.LoadAll());
        Assert.Contains("api_key=[REDACTED:credential]", stored.LastSubmitError);
        Assert.Contains(SuggestionStore.RedactionTruncationMarker, stored.LastSubmitError);
        Assert.DoesNotContain(tailSecret, stored.LastSubmitError);
        Assert.Equal(stored.LastSubmitError, result.SubmissionError);
    }

    [Fact]
    public async Task TryAddAndSubmit_SlowSubmission_DoesNotHoldFileLock()
    {
        var record = MakeRecord("other", null, "Slow remote submission");
        using var submissionStarted = new ManualResetEventSlim(false);
        using var releaseSubmission = new ManualResetEventSlim(false);
        using var callbackFinished = new ManualResetEventSlim(false);
        Exception? callbackException = null;

        var submitTask = Task.Run(() =>
        {
            try
            {
                return _store.TryAddAndSubmit(record, _ =>
                {
                    submissionStarted.Set();
                    releaseSubmission.Wait(TimeSpan.FromSeconds(5));
                    callbackFinished.Set();
                    return SuggestionStore.SubmitAttemptResult.Failure("timeout");
                });
            }
            catch (Exception ex)
            {
                callbackException = ex;
                throw;
            }
        });

        Assert.True(submissionStarted.Wait(TimeSpan.FromSeconds(5)));

        var secondStore = new SuggestionStore(_tempDir);
        var addedWhileRemoteSubmitWasBlocked = secondStore.TryAdd(
            MakeRecord("other", null, "Independent suggestion while remote submit is blocked"));

        releaseSubmission.Set();
        var result = await submitTask;

        Assert.True(addedWhileRemoteSubmitWasBlocked);
        Assert.True(callbackFinished.IsSet);
        Assert.Null(callbackException);
        Assert.Null(result.UpstreamUrl);
        Assert.Equal(2, _store.LoadAll().Count);
    }

    [Fact]
    public void TryUpdate_RejectsStaleRevisionWithoutOverwritingCurrentContent_Issue4588()
    {
        var record = MakeRecord("bug", "csharp", "Original suggestion content");
        Assert.True(_store.TryAdd(record));
        var originalRevisionHash = record.RevisionHash;

        var current = Assert.Single(_store.LoadAll());
        current.Description = "Current suggestion content";
        Assert.Equal(
            SuggestionStore.MutationResult.Success,
            _store.TryUpdate(current.Id, originalRevisionHash, current, out var updated));
        Assert.NotNull(updated);

        var stale = MakeRecord("bug", "csharp", "Stale overwrite content");
        stale.Id = current.Id;
        stale.Hash = current.Id;
        Assert.Equal(
            SuggestionStore.MutationResult.RevisionConflict,
            _store.TryUpdate(current.Id, originalRevisionHash, stale, out var rejected));

        Assert.Null(rejected);
        var saved = Assert.Single(_store.LoadAll());
        Assert.Equal(current.Id, saved.Id);
        Assert.Equal("Current suggestion content", saved.Description);
        Assert.Equal(updated!.RevisionHash, saved.RevisionHash);
    }

    [Fact]
    public void TryUpdate_ContextOnlyChangeInvalidatesStaleRevision_Issue4588()
    {
        var record = MakeRecord("bug", "csharp", "Context concurrency regression");
        record.Context = "original context";
        Assert.True(_store.TryAdd(record));
        var originalRevisionHash = record.RevisionHash;

        var current = Assert.Single(_store.LoadAll());
        current.Context = "current context";
        Assert.Equal(
            SuggestionStore.MutationResult.Success,
            _store.TryUpdate(current.Id, originalRevisionHash, current, out var updated));

        var stale = Assert.Single(_store.LoadAll());
        stale.Context = "stale context";
        Assert.Equal(
            SuggestionStore.MutationResult.RevisionConflict,
            _store.TryUpdate(current.Id, originalRevisionHash, stale, out var rejected));

        Assert.Null(rejected);
        Assert.Equal("current context", Assert.Single(_store.LoadAll()).Context);
        Assert.NotEqual(originalRevisionHash, updated!.RevisionHash);
    }

    [Fact]
    public void TryTransitionStatus_ValidatesTransitionsAndPersistsAuditMetadata_Issue4719()
    {
        var changedAt = new DateTimeOffset(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var clock = new ManualTimeProvider(changedAt);
        var store = new SuggestionStore(_tempDir, null, clock);
        var record = MakeRecord("bug", "csharp", "Manual lifecycle transition");
        Assert.True(store.TryAdd(record));
        var draftRevision = record.RevisionHash;

        Assert.Equal(
            SuggestionStore.MutationResult.InvalidTransition,
            store.TryTransitionStatus(
                record.Id,
                draftRevision,
                SuggestionStatus.OpenInUpstream,
                "maintainer",
                "No upstream issue exists",
                out var invalid));
        Assert.Null(invalid);

        Assert.Equal(
            SuggestionStore.MutationResult.Success,
            store.TryTransitionStatus(
                record.Id,
                draftRevision,
                SuggestionStatus.WontFix,
                "  widthdom  ",
                "  Outside the supported scope.  ",
                out var transitioned));

        Assert.NotNull(transitioned);
        Assert.Equal(SuggestionStatus.Draft, transitioned.PreviousStatus);
        Assert.Equal(SuggestionStatus.WontFix, transitioned.Status);
        Assert.Equal(changedAt.UtcDateTime, transitioned.StatusChangedAt);
        Assert.Equal("widthdom", transitioned.StatusChangedBy);
        Assert.Equal("Outside the supported scope.", transitioned.StatusChangeReason);
        Assert.NotEqual(draftRevision, transitioned.RevisionHash);
        Assert.Null(transitioned.ResolvedAt);

        Assert.Equal(
            SuggestionStore.MutationResult.RevisionConflict,
            store.TryTransitionStatus(
                record.Id,
                draftRevision,
                SuggestionStatus.Duplicate,
                "maintainer",
                null,
                out var stale));
        Assert.Null(stale);

        var persisted = Assert.Single(new SuggestionStore(_tempDir).LoadAll());
        Assert.Equal(transitioned.RevisionHash, persisted.RevisionHash);
        Assert.Equal(SuggestionStatus.Draft, persisted.PreviousStatus);
        Assert.Equal(SuggestionStatus.WontFix, persisted.Status);
        Assert.Equal("widthdom", persisted.StatusChangedBy);
        Assert.Equal("Outside the supported scope.", persisted.StatusChangeReason);
    }

    [Fact]
    public void TryUpdate_CannotBypassValidatedLifecycleTransition_Issue4719()
    {
        var record = MakeRecord("bug", "csharp", "Lifecycle metadata must remain store-owned");
        Assert.True(_store.TryAdd(record));

        var replacement = Assert.Single(_store.LoadAll());
        replacement.Description = "Content edit remains allowed";
        replacement.Status = SuggestionStatus.WontFix;
        replacement.PreviousStatus = SuggestionStatus.ResolvedInUpstream;
        replacement.StatusChangedAt = DateTime.UtcNow;
        replacement.StatusChangedBy = "unvalidated caller";
        replacement.StatusChangeReason = "bypass";

        Assert.Equal(
            SuggestionStore.MutationResult.Success,
            _store.TryUpdate(record.Id, record.RevisionHash, replacement, out var updated));

        Assert.NotNull(updated);
        Assert.Equal("Content edit remains allowed", updated.Description);
        Assert.Equal(SuggestionStatus.Draft, updated.Status);
        Assert.Null(updated.PreviousStatus);
        Assert.Null(updated.StatusChangedAt);
        Assert.Null(updated.StatusChangedBy);
        Assert.Null(updated.StatusChangeReason);
    }

    [Fact]
    public void TryTransitionStatus_RedactsAuditMetadataBeforePersistence_Issue4719()
    {
        const string actorSecret = "actor-secret-4719";
        const string reasonSecret = "reason-secret-4719";
        var record = MakeRecord("security", "csharp", "Audit metadata must be redacted");
        Assert.True(_store.TryAdd(record));

        Assert.Equal(
            SuggestionStore.MutationResult.Success,
            _store.TryTransitionStatus(
                record.Id,
                record.RevisionHash,
                SuggestionStatus.WontFix,
                $"api_key={actorSecret}",
                $"token={reasonSecret}",
                out var transitioned));

        Assert.NotNull(transitioned);
        Assert.Equal("api_key=[REDACTED:credential]", transitioned.StatusChangedBy);
        Assert.Equal("token=[REDACTED:credential]", transitioned.StatusChangeReason);
        var persistedJson = File.ReadAllText(Path.Combine(_tempDir, "suggestions-codeindex.json"));
        Assert.DoesNotContain(actorSecret, persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(reasonSecret, persistedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAdd_EditThenReAddOriginalContentAllocatesDistinctStableId_Issue4588()
    {
        var first = MakeRecord("bug", "csharp", "Original reusable content");
        Assert.True(_store.TryAdd(first));
        var firstId = first.Id;
        var originalRevisionHash = first.RevisionHash;

        var edited = Assert.Single(_store.LoadAll());
        edited.Description = "Edited content";
        Assert.Equal(
            SuggestionStore.MutationResult.Success,
            _store.TryUpdate(firstId, originalRevisionHash, edited, out _));

        var readded = MakeRecord("bug", "csharp", "Original reusable content");
        Assert.True(_store.TryAdd(readded));

        var saved = _store.LoadAll();
        Assert.Equal(2, saved.Count);
        Assert.Equal(2, saved.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(saved, candidate => candidate.Id == firstId && candidate.Description == "Edited content");
        Assert.Contains(saved, candidate => candidate.Id != firstId && candidate.Description == "Original reusable content");
    }

    [Fact]
    public void TryDelete_RejectsStaleRevisionWithoutDeletingNewerContent_Issue4588()
    {
        var record = MakeRecord("bug", "csharp", "Stale delete regression");
        Assert.True(_store.TryAdd(record));
        var staleRevisionHash = record.RevisionHash;

        var current = Assert.Single(_store.LoadAll());
        current.Context = "newer content that must survive";
        Assert.Equal(
            SuggestionStore.MutationResult.Success,
            _store.TryUpdate(current.Id, staleRevisionHash, current, out var updated));
        Assert.NotNull(updated);

        Assert.Equal(
            SuggestionStore.MutationResult.RevisionConflict,
            _store.TryDelete(current.Id, staleRevisionHash, out var rejected));

        Assert.Null(rejected);
        var saved = Assert.Single(_store.LoadAll());
        Assert.Equal("newer content that must survive", saved.Context);
        Assert.Equal(updated!.RevisionHash, saved.RevisionHash);
    }

    [Fact]
    public async Task TryAddAndSubmitAsync_ActiveSubmissionRejectsConcurrentMutation_Issue4588()
    {
        var record = MakeRecord("bug", "csharp", "Submission revision regression");
        var submissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitTask = _store.TryAddAndSubmitAsync(
            record,
            async (_, _) =>
            {
                submissionStarted.SetResult();
                await releaseSubmission.Task;
                return SuggestionStore.SubmitAttemptResult.Success("https://github.com/Widthdom/CodeIndex/issues/9999");
            },
            CancellationToken.None);

        await submissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = Assert.Single(_store.LoadAll());
        var submittedRevision = current.RevisionHash;
        current.Context = "edited while submission was in flight";
        Assert.Equal(
            SuggestionStore.MutationResult.SubmissionInFlight,
            _store.TryUpdate(current.Id, submittedRevision, current, out var rejected));
        Assert.Null(rejected);
        Assert.Equal(
            SuggestionStore.MutationResult.SubmissionInFlight,
            _store.TryDelete(current.Id, submittedRevision, out var rejectedDelete));
        Assert.Null(rejectedDelete);

        releaseSubmission.SetResult();
        var result = await submitTask;

        Assert.Equal("https://github.com/Widthdom/CodeIndex/issues/9999", result.UpstreamUrl);
        Assert.Null(result.SubmissionError);
        var saved = Assert.Single(_store.LoadAll());
        Assert.Equal(SuggestionStatus.SubmittedPendingTriage, saved.Status);
        Assert.NotEqual(submittedRevision, saved.RevisionHash);
        Assert.Equal(SuggestionStatus.Draft, saved.PreviousStatus);
        Assert.Equal("github_submission", saved.StatusChangedBy);
        Assert.Equal("GitHub issue submission succeeded.", saved.StatusChangeReason);
        Assert.Null(saved.Context);
    }

    [Fact]
    public async Task TryAddAndSubmitAsync_ExpiredReservationEditDoesNotStampOldSubmissionOntoNewRevision_Issue4588()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2032, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var record = MakeRecord("bug", "csharp", "Expired submission revision regression");
        var submissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitTask = store.TryAddAndSubmitAsync(
            record,
            async (_, _) =>
            {
                submissionStarted.SetResult();
                await releaseSubmission.Task;
                return SuggestionStore.SubmitAttemptResult.Success("https://github.com/Widthdom/CodeIndex/issues/9998");
            },
            CancellationToken.None);

        await submissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = Assert.Single(store.LoadAll());
        var submittedRevision = current.RevisionHash;
        clock.Advance(TimeSpan.FromMinutes(7));
        current.Context = "edited after the submission reservation expired";
        Assert.Equal(
            SuggestionStore.MutationResult.Success,
            store.TryUpdate(current.Id, submittedRevision, current, out var updated));

        releaseSubmission.SetResult();
        var result = await submitTask;

        Assert.Null(result.UpstreamUrl);
        Assert.Contains("changed while GitHub submission was in flight", result.SubmissionError);
        var saved = Assert.Single(store.LoadAll());
        Assert.Equal(SuggestionStatus.Draft, saved.Status);
        Assert.Null(saved.UpstreamUrl);
        Assert.Equal(updated!.RevisionHash, saved.RevisionHash);
        Assert.Equal("edited after the submission reservation expired", saved.Context);
    }

    [Fact]
    public void TryAddAndSubmit_RateLimitFailure_StampsNextRetryAt()
    {
        var record = MakeRecord("other", null, "Submission is rate limited");
        var nextRetryAt = DateTime.UtcNow.AddMinutes(10);

        var result = _store.TryAddAndSubmit(record,
            _ => SuggestionStore.SubmitAttemptResult.RetryAfter("429: rate limited", nextRetryAt));

        var stored = Assert.Single(_store.LoadAll());
        Assert.True(result.IsNew);
        Assert.Null(result.UpstreamUrl);
        Assert.Equal(SuggestionStatus.Draft, stored.Status);
        Assert.Equal(1, stored.SubmitAttemptCount);
        Assert.Equal("429: rate limited", stored.LastSubmitError);
        Assert.Equal(nextRetryAt, stored.NextRetryAt);
    }

    [Fact]
    public void TryAddAndSubmit_DuplicateBeforeNextRetryAt_DoesNotRetry()
    {
        var record = MakeRecord("other", null, "Duplicate waits for retry");
        var nextRetryAt = DateTime.UtcNow.AddHours(1);
        _store.TryAddAndSubmit(record,
            _ => SuggestionStore.SubmitAttemptResult.RetryAfter("429: rate limited", nextRetryAt));

        var duplicate = MakeRecord("other", null, "Duplicate waits for retry");
        var callbackCalls = 0;
        _store.TryAddAndSubmit(duplicate, _ =>
        {
            callbackCalls++;
            return SuggestionStore.SubmitAttemptResult.Success("https://github.com/widthdom/CodeIndex/issues/123");
        });

        var stored = Assert.Single(_store.LoadAll());
        Assert.Equal(0, callbackCalls);
        Assert.Equal(1, stored.SubmitAttemptCount);
        Assert.Equal(nextRetryAt, stored.NextRetryAt);
        Assert.Null(stored.UpstreamUrl);
    }

    [Fact]
    public void TryAddAndSubmit_DuplicateAfterNextRetryAt_RetriesAndClearsNextRetryAtOnSuccess()
    {
        var record = MakeRecord("other", null, "Duplicate retries after window");
        _store.TryAddAndSubmit(record,
            _ => SuggestionStore.SubmitAttemptResult.RetryAfter("429: rate limited", DateTime.UtcNow.AddMinutes(-1)));

        var duplicate = MakeRecord("other", null, "Duplicate retries after window");
        _store.TryAddAndSubmit(duplicate,
            _ => SuggestionStore.SubmitAttemptResult.Success("https://github.com/widthdom/CodeIndex/issues/123"));

        var stored = Assert.Single(_store.LoadAll());
        Assert.Equal(2, stored.SubmitAttemptCount);
        Assert.Null(stored.NextRetryAt);
        Assert.Equal("https://github.com/widthdom/CodeIndex/issues/123", stored.UpstreamUrl);
    }

    [Fact]
    public void TryAddAndSubmit_Exception_StampsExceptionTypeAndMessage()
    {
        var record = MakeRecord("other", null, "Submission throws");

        var result = _store.TryAddAndSubmit(record,
            _ => throw new InvalidOperationException("network unavailable"));

        var stored = Assert.Single(_store.LoadAll());
        Assert.True(result.IsNew);
        Assert.Null(result.UpstreamUrl);
        Assert.Equal(SuggestionStatus.Draft, stored.Status);
        Assert.Equal(1, stored.SubmitAttemptCount);
        Assert.NotNull(stored.LastSubmitAttempt);
        Assert.Equal("InvalidOperationException: network unavailable", stored.LastSubmitError);
    }

    [Fact]
    public void TryAddAndSubmit_DuplicateUnsubmitted_RetriesAndIncrementsAttemptCount()
    {
        var record = MakeRecord("other", null, "Retry duplicate");
        _store.TryAddAndSubmit(record,
            _ => SuggestionStore.SubmitAttemptResult.Failure("500: unavailable"));

        var duplicate = MakeRecord("other", null, "Retry duplicate");
        _store.TryAddAndSubmit(duplicate,
            _ => SuggestionStore.SubmitAttemptResult.Failure("422: validation failed"));

        var stored = Assert.Single(_store.LoadAll());
        Assert.Equal(2, stored.SubmitAttemptCount);
        Assert.Equal("422: validation failed", stored.LastSubmitError);
    }

    [Fact]
    public void LoadByStatus_ReturnsOnlyMatchingLifecycleState()
    {
        var submitted = MakeRecord("other", null, "Submitted suggestion");
        submitted.Status = SuggestionStatus.SubmittedPendingTriage;
        submitted.UpstreamIssueNumber = 1;
        submitted.UpstreamUrl = "https://github.com/widthdom/CodeIndex/issues/1";
        var draft = MakeRecord("other", null, "Draft suggestion");

        _store.TryAdd(submitted);
        _store.TryAdd(draft);

        var loaded = _store.LoadByStatus(SuggestionStatus.Draft);

        Assert.Single(loaded);
        Assert.Equal(draft.Hash, loaded[0].Hash);
    }

    [Fact]
    public void LoadSince_ReturnsSuggestionsAtOrAfterThreshold()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2031, 5, 1, 9, 0, 0, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var older = MakeRecord("other", null, "Older suggestion");
        var boundary = MakeRecord("other", null, "Boundary suggestion");
        var newer = MakeRecord("other", null, "Newer suggestion");

        store.TryAdd(older);
        clock.SetUtcNow(new DateTimeOffset(2031, 5, 2, 9, 0, 0, TimeSpan.Zero));
        store.TryAdd(boundary);
        clock.SetUtcNow(new DateTimeOffset(2031, 5, 3, 9, 0, 0, TimeSpan.Zero));
        store.TryAdd(newer);

        var loaded = store.LoadSince(new DateTimeOffset(2031, 5, 2, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[] { boundary.Hash, newer.Hash }, loaded.Select(s => s.Hash));
    }

    [Fact]
    public void LoadSince_NormalizesLocalAndUnspecifiedCreatedAt_Issue4321()
    {
        var threshold = new DateTimeOffset(2031, 5, 2, 9, 0, 0, TimeSpan.Zero);
        var older = MakeRecord("other", null, "Older timestamp suggestion");
        older.CreatedAt = threshold.UtcDateTime.AddTicks(-1);
        var local = MakeRecord("other", null, "Local timestamp suggestion");
        local.CreatedAt = threshold.UtcDateTime.ToLocalTime();
        var unspecified = MakeRecord("other", null, "Unspecified timestamp suggestion");
        unspecified.CreatedAt = new DateTime(2031, 5, 2, 9, 0, 0, DateTimeKind.Unspecified);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "suggestions-codeindex.json"),
            JsonSerializer.Serialize(new[] { older, local, unspecified }, options));

        var loaded = _store.LoadSince(threshold).Select(s => s.Hash).ToArray();

        Assert.DoesNotContain(older.Hash, loaded);
        Assert.Contains(local.Hash, loaded);
        Assert.Contains(unspecified.Hash, loaded);
    }

    [Fact]
    public void LoadByCategory_IsCaseInsensitive()
    {
        var crash = MakeRecord("crash_report", "csharp", "Crash suggestion");
        var other = MakeRecord("other", "csharp", "Other suggestion");

        _store.TryAdd(crash);
        _store.TryAdd(other);

        var loaded = _store.LoadByCategory("CRASH_REPORT");

        Assert.Single(loaded);
        Assert.Equal(crash.Hash, loaded[0].Hash);
    }

    [Fact]
    public void LoadByLanguage_IsCaseInsensitive()
    {
        var csharp = MakeRecord("other", "csharp", "CSharp suggestion");
        var python = MakeRecord("other", "python", "Python suggestion");

        _store.TryAdd(csharp);
        _store.TryAdd(python);

        var loaded = _store.LoadByLanguage("CSHARP");

        Assert.Single(loaded);
        Assert.Equal(csharp.Hash, loaded[0].Hash);
    }

    [Fact]
    public void Load_ReturnsRequestedPageInStoredOrder()
    {
        var first = MakeRecord("other", null, "First suggestion");
        var second = MakeRecord("other", null, "Second suggestion");
        var third = MakeRecord("other", null, "Third suggestion");
        var fourth = MakeRecord("other", null, "Fourth suggestion");

        _store.TryAdd(first);
        _store.TryAdd(second);
        _store.TryAdd(third);
        _store.TryAdd(fourth);

        var loaded = _store.Load(skip: 1, take: 2);

        Assert.Equal(new[] { second.Hash, third.Hash }, loaded.Select(s => s.Hash));
    }

    [Fact]
    public void Load_FilteredCorruptJson_ReturnsEmptyListAndPreservesBackup()
    {
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var backupPath = filePath + ".bak";
        File.WriteAllText(filePath, "{not valid json[[[");

        var loaded = _store.LoadByCategory("other");

        Assert.Empty(loaded);
        Assert.True(File.Exists(backupPath), "Corrupt file should be preserved as .bak");
        Assert.False(File.Exists(filePath), "Original corrupt file should be removed");
    }

    [Fact]
    public void Load_FilteredWhitespaceOnlyFile_ReturnsEmptyListWithoutBackup()
    {
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var backupPath = filePath + ".bak";
        File.WriteAllText(filePath, " \r\n\t ");

        var loaded = _store.LoadByLanguage("csharp");

        Assert.Empty(loaded);
        Assert.True(File.Exists(filePath), "Whitespace-only file should remain the live store.");
        Assert.False(File.Exists(backupPath), "Whitespace-only file should not be treated as corrupt.");
    }

    [Fact]
    public void Load_FilteredReadDoesNotBlockSubsequentReplacementWrite()
    {
        _store.TryAdd(MakeRecord("other", null, "Existing suggestion"));

        var loaded = _store.LoadByStatus(SuggestionStatus.Draft);

        var record = MakeRecord("other", null, "Concurrent write suggestion");

        var ex = Record.Exception(() => _store.TryAdd(record));

        Assert.Single(loaded);
        Assert.Null(ex);
        Assert.Contains(_store.LoadAll(), s => s.Hash == record.Hash);
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
        Assert.Equal(SuggestionStatus.SubmittedPendingTriage, all[0].Status);
        Assert.Equal(99, all[0].UpstreamIssueNumber);
        Assert.Equal("https://github.com/widthdom/CodeIndex/issues/99", all[0].UpstreamUrl);
        Assert.NotNull(all[0].LastSyncedAt);
    }

    [Fact]
    public void MarkSubmitted_NonexistentHash_DoesNothing()
    {
        var record = MakeRecord("other", null, "Some suggestion");
        _store.TryAdd(record);

        _store.MarkSubmitted("nonexistent_hash", "https://example.com");

        var all = _store.LoadAll();
        Assert.Single(all);
        Assert.Equal(SuggestionStatus.Draft, all[0].Status);
        Assert.Null(all[0].UpstreamUrl);
    }

    [Fact]
    public void LoadAll_LegacySubmittedFlag_MigratesToLifecycleFields()
    {
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        File.WriteAllText(filePath, """
[
  {
    "category": "other",
    "description": "Legacy suggestion",
    "hash": "abc123",
    "created_at": "2026-04-12T10:00:00Z",
    "submitted_to_github": true,
    "github_issue_url": "https://github.com/widthdom/CodeIndex/issues/123"
  }
]
""");

        var all = _store.LoadAll();

        Assert.Single(all);
        Assert.Equal(SuggestionStatus.SubmittedPendingTriage, all[0].Status);
        Assert.Equal(123, all[0].UpstreamIssueNumber);
        Assert.Equal("https://github.com/widthdom/CodeIndex/issues/123", all[0].UpstreamUrl);
        Assert.Null(all[0].SubmittedToGitHub);
        Assert.Null(all[0].GitHubIssueUrl);
    }

    [Fact]
    public void MarkSubmitted_WritesLifecycleFieldsWithoutLegacyFields()
    {
        var record = MakeRecord("symbol_extraction", "csharp", "Missing record support");
        _store.TryAdd(record);

        _store.MarkSubmitted(record.Hash, "https://github.com/widthdom/CodeIndex/issues/99");

        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var json = File.ReadAllText(filePath);
        Assert.Contains("\"status\": \"submitted_pending_triage\"", json);
        Assert.Contains("\"upstream_issue_number\": 99", json);
        Assert.Contains("\"upstream_url\": \"https://github.com/widthdom/CodeIndex/issues/99\"", json);
        Assert.DoesNotContain("submitted_to_github", json);
        Assert.DoesNotContain("github_issue_url", json);
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
    public void CorruptFile_ExistingBackupIsNotOverwritten()
    {
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var backupPath = filePath + ".bak";
        File.WriteAllText(backupPath, "first corrupt backup");
        File.WriteAllText(filePath, "{second corrupt json[[[");

        var all = _store.LoadAll();

        Assert.Empty(all);
        Assert.Equal("first corrupt backup", File.ReadAllText(backupPath));
        var timestampedBackup = Assert.Single(
            Directory
                .EnumerateFiles(_tempDir, "suggestions-codeindex.json.bak.*")
                .Where(path => !string.Equals(path, backupPath, StringComparison.Ordinal)));
        Assert.Contains("{second corrupt json[[[", File.ReadAllText(timestampedBackup));
        Assert.False(File.Exists(filePath), "Original corrupt file should be removed");
    }

    [Fact]
    public void CorruptFile_OnPosixHardensBackupFileMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var backupPath = filePath + ".bak";
        File.WriteAllText(filePath, "{corrupt json[[[");
#pragma warning disable CA1416
        File.SetUnixFileMode(
            filePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
#pragma warning restore CA1416

        var all = _store.LoadAll();

        Assert.Empty(all);
        Assert.True(File.Exists(backupPath), "Corrupt file should be preserved as .bak");
        AssertPrivateFileMode(backupPath);
    }

    [Fact]
    public void ZeroByteFile_IsPreservedAsBackup()
    {
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var backupPath = filePath + ".bak";
        File.WriteAllBytes(filePath, Array.Empty<byte>());

        var all = _store.LoadAll();

        Assert.Empty(all);
        Assert.True(File.Exists(backupPath), "Zero-byte file should be preserved as .bak");
        Assert.False(File.Exists(filePath), "Original zero-byte file should be removed");
    }

    [Fact]
    public void FilteredZeroByteFile_IsPreservedAsBackup()
    {
        var filePath = Path.Combine(_tempDir, "suggestions-codeindex.json");
        var backupPath = filePath + ".bak";
        File.WriteAllBytes(filePath, Array.Empty<byte>());

        var all = _store.LoadByCategory("other");

        Assert.Empty(all);
        Assert.True(File.Exists(backupPath), "Zero-byte file should be preserved as .bak");
        Assert.False(File.Exists(filePath), "Original zero-byte file should be removed");
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
    public void TryAdd_PrunesStaleRecordsToArchive()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var old = MakeRecord("other", null, "Old suggestion");
        Assert.True(store.TryAdd(old));

        clock.SetUtcNow(new DateTimeOffset(2032, 2, 5, 0, 0, 0, TimeSpan.Zero));
        var fresh = MakeRecord("other", null, "Fresh suggestion");
        Assert.True(store.TryAdd(fresh));

        var all = store.LoadAll();
        Assert.Single(all);
        Assert.Equal("Fresh suggestion", all[0].Description);

        var archivePath = Path.Combine(_tempDir, "suggestions-codeindex.archive.jsonl");
        Assert.True(File.Exists(archivePath));
        var archive = File.ReadAllText(archivePath);
        Assert.Contains("Old suggestion", archive);
    }

    [Fact]
    public void TryAdd_OnPosixCreatesPrivateArchiveFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var clock = new ManualTimeProvider(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var old = MakeRecord("other", null, "Old suggestion");
        Assert.True(store.TryAdd(old));

        clock.SetUtcNow(new DateTimeOffset(2032, 2, 5, 0, 0, 0, TimeSpan.Zero));
        var fresh = MakeRecord("other", null, "Fresh suggestion");
        Assert.True(store.TryAdd(fresh));

        AssertPrivateFileMode(Path.Combine(_tempDir, "suggestions-codeindex.archive.jsonl"));
    }

    [Fact]
    public void TryAdd_RotatesArchiveWhenCapWouldBeExceeded()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var old = MakeRecord("other", null, "Old suggestion");
        Assert.True(store.TryAdd(old));

        var archivePath = Path.Combine(_tempDir, "suggestions-codeindex.archive.jsonl");
        CreateSparseFile(archivePath, SuggestionStore.MaxSuggestionArchiveBytes);

        clock.SetUtcNow(new DateTimeOffset(2032, 2, 5, 0, 0, 0, TimeSpan.Zero));
        var fresh = MakeRecord("other", null, "Fresh suggestion");
        Assert.True(store.TryAdd(fresh));

        var rotatedPath = archivePath + ".1";
        Assert.True(File.Exists(rotatedPath));
        Assert.Equal(SuggestionStore.MaxSuggestionArchiveBytes, new FileInfo(rotatedPath).Length);
        Assert.Contains("Old suggestion", File.ReadAllText(archivePath));
        Assert.True(new FileInfo(archivePath).Length <= SuggestionStore.MaxSuggestionArchiveBytes);
    }

    [Fact]
    public void BuildBoundedArchiveLines_DropsOversizedRecordWithBoundedDiagnostics()
    {
        var oversized = MakeRecord(
            "other",
            null,
            new string('x', SuggestionStore.MaxSuggestionArchiveBytes + 1));

        var archive = SuggestionStore.BuildBoundedArchiveLines([oversized]);

        Assert.Empty(archive.Lines);
        Assert.Equal(0, archive.DroppedByCapCount);
        Assert.Equal(1, archive.OversizedDroppedCount);
        Assert.True(archive.LargestOversizedRecordBytes > SuggestionStore.MaxSuggestionArchiveBytes);
    }

    [Fact]
    public void TryAdd_DuplicateStillPersistsPrunedRecords()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var old = MakeRecord("other", null, "Old suggestion");
        var duplicate = MakeRecord("other", null, "Duplicate suggestion");
        Assert.True(store.TryAdd(old));
        clock.SetUtcNow(new DateTimeOffset(2032, 2, 5, 0, 0, 0, TimeSpan.Zero));
        Assert.True(store.TryAdd(duplicate));

        Assert.False(store.TryAdd(MakeRecord("other", null, "Duplicate suggestion")));

        var all = store.LoadAll();
        Assert.Single(all);
        Assert.Equal("Duplicate suggestion", all[0].Description);
        var archivePath = Path.Combine(_tempDir, "suggestions-codeindex.archive.jsonl");
        Assert.Equal(1, File.ReadAllText(archivePath).Split("Old suggestion").Length - 1);
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
        Directory.CreateDirectory(filePath);

        var record = MakeRecord("other", null, "Move failure cleanup");
        var ex = Record.Exception(() => _store.TryAdd(record));

        Assert.NotNull(ex);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_tempDir),
            file => Path.GetFileName(file).EndsWith(".tmp", StringComparison.Ordinal));
    }

    // --- Helpers / ヘルパー ---

    private static string BuildDeepSuggestionStoreJson(int nestedObjectCount)
    {
        var builder = new StringBuilder();
        builder.Append("[{\"category\":\"other\",\"description\":\"Deep suggestion\",\"hash\":\"deep\",\"ignored\":");
        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append("{\"x\":");
        builder.Append("\"value\"");
        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append('}');
        builder.Append("}]");
        return builder.ToString();
    }

    private static void WriteEmptyRecordStore(string path, int count)
    {
        var builder = new StringBuilder(capacity: (count * 3) + 2);
        builder.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append("{}");
        }

        builder.Append(']');
        File.WriteAllText(path, builder.ToString());
    }

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

    private static void AssertPrivateFileMode(string path)
    {
#pragma warning disable CA1416
        Assert.Equal(
            DataDirectorySecurity.PrivateFileMode,
            File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits);
#pragma warning restore CA1416
    }

    public void Dispose()
    {
        TestProjectHelper.DeleteDirectory(_tempDir);
    }
}

[Collection("SQLite pool sensitive")]
public sealed class SuggestionStoreEnvironmentTests : IDisposable
{
    private readonly string _tempDir = TestProjectHelper.CreateTempProject("suggestion_store_environment");

    [Fact]
    public void TryAdd_PrunesOldestRecordsOverConfiguredMaxCount()
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.MaxCountEnvironmentVariable);
        env.Set(SuggestionStore.MaxCountEnvironmentVariable, "2");
        var store = new SuggestionStore(_tempDir);
        var first = MakeRecord("First suggestion", -3);
        var second = MakeRecord("Second suggestion", -2);
        var third = MakeRecord("Third suggestion", -1);

        Assert.True(store.TryAdd(first));
        Assert.True(store.TryAdd(second));
        Assert.True(store.TryAdd(third));

        var all = store.LoadAll();
        Assert.Equal(new[] { "Second suggestion", "Third suggestion" }, all.Select(record => record.Description));
        Assert.Contains("First suggestion", File.ReadAllText(Path.Combine(_tempDir, "suggestions-codeindex.archive.jsonl")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveMaxAge_EnforcesConfiguredBoundary(bool atMaximum)
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.MaxAgeDaysEnvironmentVariable);
        var configured = atMaximum ? SuggestionStore.MaximumMaxAgeDays : SuggestionStore.MaximumMaxAgeDays + 1;
        env.Set(SuggestionStore.MaxAgeDaysEnvironmentVariable, configured.ToString(CultureInfo.InvariantCulture));

        var expected = atMaximum ? SuggestionStore.MaximumMaxAgeDays : SuggestionStore.DefaultMaxAgeDays;
        Assert.Equal(TimeSpan.FromDays(expected), SuggestionStore.ResolveMaxAge());
    }

    [Fact]
    public void ResolveMaxAge_IgnoresCurrentCulturePositiveSign_Issue3404()
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.MaxAgeDaysEnvironmentVariable);
        using var _ = new CultureScope(TestCultures.BuildCaretPositiveSignCulture());
        env.Set(SuggestionStore.MaxAgeDaysEnvironmentVariable, "^30");

        Assert.Equal(TimeSpan.FromDays(SuggestionStore.DefaultMaxAgeDays), SuggestionStore.ResolveMaxAge());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveMaxCount_EnforcesConfiguredBoundary(bool atMaximum)
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.MaxCountEnvironmentVariable);
        var configured = atMaximum ? SuggestionStore.MaximumMaxCount : SuggestionStore.MaximumMaxCount + 1;
        env.Set(SuggestionStore.MaxCountEnvironmentVariable, configured.ToString(CultureInfo.InvariantCulture));

        var expected = atMaximum ? SuggestionStore.MaximumMaxCount : SuggestionStore.DefaultMaxCount;
        Assert.Equal(expected, SuggestionStore.ResolveMaxCount());
    }

    [Fact]
    public void ResolveMaxCount_IgnoresCurrentCulturePositiveSign_Issue3404()
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.MaxCountEnvironmentVariable);
        using var _ = new CultureScope(TestCultures.BuildCaretPositiveSignCulture());
        env.Set(SuggestionStore.MaxCountEnvironmentVariable, "^42");

        Assert.Equal(SuggestionStore.DefaultMaxCount, SuggestionStore.ResolveMaxCount());
    }

    private static SuggestionRecord MakeRecord(string description, int ageMinutes)
        => new()
        {
            Category = "other",
            Description = description,
            Hash = SuggestionStore.ComputeHash("other", null, description),
            CreatedAt = DateTime.UtcNow.AddMinutes(ageMinutes),
        };

    public void Dispose() => TestProjectHelper.DeleteDirectory(_tempDir);
}
