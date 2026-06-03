using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for SuggestionStore (local JSON storage with deduplication).
/// SuggestionStoreのテスト（ローカルJSON蓄積 + 重複排除）。
/// </summary>
[Collection("SQLite pool sensitive")]
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
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', SuggestionStore.MaxSuggestionStoreBytes + 1).ToArray());

        var records = _store.LoadAll();

        Assert.Empty(records);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LoadByStatus_OversizedStore_PreservesBackupAndReturnsEmpty()
    {
        var path = Path.Combine(_tempDir, "suggestions-codeindex.json");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', SuggestionStore.MaxSuggestionStoreBytes + 1).ToArray());

        var records = _store.LoadByStatus(SuggestionStatus.Draft);

        Assert.Empty(records);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
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
            "AWS AKIA1234567890ABCDEF and password=swordfish and Bearer AbCdEfGhIjKlMnOpQrStUvWxYz123456 should not persist");
        record.Context = "token aaBB11ccDD22eeFF33ggHH44iiJJ55kk";
        record.ToolInvocationContext = "secret=hunter2";

        Assert.True(_store.TryAdd(record));

        var stored = Assert.Single(_store.LoadAll());
        Assert.Contains("[REDACTED:aws_access_key]", stored.Description);
        Assert.Contains("password=[REDACTED:credential]", stored.Description);
        Assert.Contains("[REDACTED:bearer_token]", stored.Description);
        Assert.Contains("[REDACTED:high_entropy_token]", stored.Context);
        Assert.Contains("secret=[REDACTED:credential]", stored.ToolInvocationContext);
        Assert.DoesNotContain("AKIA1234567890ABCDEF", stored.Description);
        Assert.DoesNotContain("swordfish", stored.Description);
        Assert.DoesNotContain("hunter2", stored.ToolInvocationContext);
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
    public void TryAdd_PrunesOldestRecordsOverConfiguredMaxCount()
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.MaxCountEnvironmentVariable);
        env.Set(SuggestionStore.MaxCountEnvironmentVariable, "2");
        var first = MakeRecord("other", null, "First suggestion");
        first.CreatedAt = DateTime.UtcNow.AddMinutes(-3);
        var second = MakeRecord("other", null, "Second suggestion");
        second.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        var third = MakeRecord("other", null, "Third suggestion");
        third.CreatedAt = DateTime.UtcNow.AddMinutes(-1);

        Assert.True(_store.TryAdd(first));
        Assert.True(_store.TryAdd(second));
        Assert.True(_store.TryAdd(third));

        var all = _store.LoadAll();
        Assert.Equal(new[] { "Second suggestion", "Third suggestion" }, all.Select(record => record.Description));
        Assert.Contains("First suggestion", File.ReadAllText(Path.Combine(_tempDir, "suggestions-codeindex.archive.jsonl")));
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
