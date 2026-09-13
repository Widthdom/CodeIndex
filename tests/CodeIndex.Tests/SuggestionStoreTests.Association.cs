using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class SuggestionStoreTests
{
    [Fact]
    public void TryLinkIssue_RoundTripsMultipleSuggestionsWithoutSubmitting_Issue5350()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2035, 6, 7, 8, 9, 10, TimeSpan.Zero));
        var store = new SuggestionStore(_tempDir, null, clock);
        var first = MakeRecord("bug", "csharp", "Manual publication after a failed request");
        store.TryAddAndSubmit(first, _ => SuggestionStore.SubmitAttemptResult.Failure("Offline"));
        var second = MakeRecord("performance", "rust", "Several observations share one upstream issue");
        Assert.True(store.TryAdd(second));
        var unrelated = MakeRecord("other", null, "Unrelated historical draft");
        unrelated.Context = "Published as #5350; context alone must not associate this record.";
        Assert.True(store.TryAdd(unrelated));
        first = store.LoadAll().Single(record => record.Id == first.Id);
        var originalAttempt = first.LastSubmitAttempt;

        Assert.Equal(SuggestionStore.MutationResult.Success,
            store.TryLinkIssue(first.Id, first.RevisionHash, "Widthdom/CodeIndex", "5350", "maintainer", "Manual publication", out var linked));
        Assert.NotNull(linked);
        Assert.Equal("https://github.com/widthdom/codeindex/issues/5350", linked.UpstreamUrl);
        Assert.Equal(5350, linked.UpstreamIssueNumber);
        Assert.Equal(SuggestionStatus.SubmittedPendingTriage, linked.Status);
        Assert.Equal(SuggestionStatus.Draft, linked.PreviousStatus);
        Assert.Equal(1, linked.SubmitAttemptCount);
        Assert.Equal(originalAttempt, linked.LastSubmitAttempt);
        Assert.Equal("Offline", linked.LastSubmitError);
        Assert.Null(linked.LastSyncedAt);
        Assert.Null(linked.ResolvedAt);
        Assert.Null(linked.NextRetryAt);
        Assert.NotEqual(first.RevisionHash, linked.RevisionHash);
        var association = Assert.IsType<SuggestionUpstreamAssociation>(linked.UpstreamAssociation);
        Assert.Equal("widthdom/codeindex", association.Repository);
        Assert.Equal("manual_external", association.Provenance);
        Assert.Equal("not_performed", association.Verification);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, association.LinkedAt);
        Assert.Equal("maintainer", association.LinkedBy);
        Assert.Equal("Manual publication", association.Reason);

        Assert.Equal(SuggestionStore.MutationResult.Success,
            store.TryLinkIssue(second.Id, second.RevisionHash, "widthdom/codeindex",
                "https://github.com/Widthdom/CodeIndex/issues/5350", null, null, out var secondLinked));
        Assert.Equal(0, secondLinked!.SubmitAttemptCount);
        Assert.Null(secondLinked.LastSubmitAttempt);
        Assert.Null(secondLinked.ResolvedAt);
        var beforeRepeat = File.ReadAllBytes(store.FilePath);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(SuggestionStore.MutationResult.Success,
            store.TryLinkIssue(first.Id, first.RevisionHash, "WIDTHDOM/CODEINDEX", "5350", "different actor", "different reason", out var repeated));
        Assert.Equal(linked.UpstreamAssociation, repeated!.UpstreamAssociation);
        Assert.Equal(beforeRepeat, File.ReadAllBytes(store.FilePath));

        var reopened = new SuggestionStore(_tempDir);
        var records = reopened.LoadAll();
        Assert.Equal(2, records.Count(record => record.UpstreamIssueNumber == 5350));
        Assert.Equal(association, records.Single(record => record.Id == first.Id).UpstreamAssociation);
        var historical = records.Single(record => record.Id == unrelated.Id);
        Assert.Equal(SuggestionStatus.Draft, historical.Status);
        Assert.Null(historical.UpstreamAssociation);
        Assert.Null(historical.UpstreamUrl);
        Assert.Equal(unrelated.Context, historical.Context);
        var createCalls = 0;
        foreach (var record in new[] { first, second })
        {
            var result = store.TryAddAndSubmit(MakeRecord(record.Category, record.Language, record.Description), _ =>
            {
                createCalls++;
                return SuggestionStore.SubmitAttemptResult.Success("https://github.com/widthdom/codeindex/issues/9999");
            });
            Assert.True(result.AlreadySubmitted);
            Assert.Equal(linked.UpstreamUrl, result.UpstreamUrl);
        }
        Assert.Equal(0, createCalls);

        Assert.Equal(SuggestionStore.MutationResult.Success,
            store.TryTransitionStatus(first.Id, linked.RevisionHash, SuggestionStatus.ResolvedInUpstream, "maintainer", "Verified separately", out var resolved));
        Assert.Equal(SuggestionStore.MutationResult.Success,
            store.TryLinkIssue(first.Id, resolved!.RevisionHash, "widthdom/codeindex", "5350", null, null, out var stillResolved));
        Assert.Equal(SuggestionStatus.ResolvedInUpstream, stillResolved!.Status);
        Assert.Equal(resolved.ResolvedAt, stillResolved.ResolvedAt);
        Assert.Equal(association, stillResolved.UpstreamAssociation);
    }

    [Theory]
    [InlineData("owner/repo", "0")]
    [InlineData("owner/repo", "01")]
    [InlineData("owner/repo", "2147483648")]
    [InlineData("owner/repo", "https://github.com/other/repo/issues/1")]
    [InlineData("owner/repo", "https://github.com/owner/repo/pull/1")]
    [InlineData("owner/repo", "https://example.com/owner/repo/issues/1")]
    [InlineData("owner/repo", "http://github.com/owner/repo/issues/1")]
    [InlineData("owner/repo", "https://github.com:443/owner/repo/issues/1")]
    [InlineData("owner/repo", "https://user@github.com/owner/repo/issues/1")]
    [InlineData("owner/repo", "https://github.com/owner/repo/issues/1#issuecomment-2")]
    [InlineData("owner/repo", "https://github.com/owner/repo/issues/1?x=1")]
    [InlineData("owner/repo", "https://github.com/owner/else/../repo/issues/1")]
    [InlineData("owner/repo", "https://github.com/owner/repo/issues/%31")]
    [InlineData("owner/repo", "https://github.com/owner/repo/issues/1/")]
    [InlineData("owner/repo", "1\n")]
    [InlineData("owner/repo", "１")]
    [InlineData("-owner/repo", "1")]
    [InlineData("owner/..", "1")]
    [InlineData("owner/other/repo", "1")]
    [InlineData("owner/repo ", "1")]
    [InlineData("öwner/repo", "1")]
    public void TryLinkIssue_RejectsInvalidIdentityWithoutChangingStore_Issue5350(string repository, string issue)
    {
        var record = MakeRecord("bug", null, "Validation must precede mutation");
        Assert.True(_store.TryAdd(record));
        var before = File.ReadAllBytes(_store.FilePath);
        Assert.Equal(SuggestionStore.MutationResult.InvalidAssociation,
            _store.TryLinkIssue(record.Id, record.RevisionHash, repository, issue, null, null, out var rejected));
        Assert.Null(rejected);
        Assert.Equal(before, File.ReadAllBytes(_store.FilePath));
    }

    [Fact]
    public void TryLinkIssue_RejectsConflictsAndPreservesLegacyHistory_Issue5350()
    {
        TestProjectHelper.WriteTextFile(_tempDir, "suggestions-codeindex.json", """
            [{"hash":"legacy-draft","category":"bug","description":"Legacy unsubmitted record"},
             {"hash":"legacy-published","category":"other","description":"Legacy publication",
              "submitted_to_github":true,"github_issue_url":"https://github.com/owner/repo/issues/1"}]
            """);
        var records = _store.LoadAll();
        var draft = records.Single(record => record.Id == "legacy-draft");
        var published = records.Single(record => record.Id == "legacy-published");
        Assert.Null(published.UpstreamAssociation);
        Assert.Equal(SuggestionStore.MutationResult.Success,
            _store.TryLinkIssue(draft.Id, draft.RevisionHash, "owner/repo", "1", "api_key=actor-secret-5350", "token=reason-secret-5350", out var linked));
        Assert.NotNull(linked!.UpstreamAssociation);
        Assert.DoesNotContain("actor-secret-5350", File.ReadAllText(_store.FilePath));
        Assert.DoesNotContain("reason-secret-5350", File.ReadAllText(_store.FilePath));
        var before = File.ReadAllBytes(_store.FilePath);
        foreach (var (repository, issue) in new[] { ("owner/repo", "2"), ("other/repo", "1") })
        {
            Assert.Equal(SuggestionStore.MutationResult.AssociationConflict,
                _store.TryLinkIssue(draft.Id, linked.RevisionHash, repository, issue, null, null, out var conflict));
            Assert.Null(conflict);
        }
        Assert.Equal(SuggestionStore.MutationResult.Success,
            _store.TryLinkIssue(published.Id, published.RevisionHash, "owner/repo", "1", "external", null, out var unchanged));
        Assert.Null(unchanged!.UpstreamAssociation); // Do not relabel historical cdidx submissions as external.
        Assert.Equal(before, File.ReadAllBytes(_store.FilePath));
    }

    [Fact]
    public void TryLinkIssue_RejectsOversizedResultWithoutQuarantiningOldStore_Issue5350()
    {
        const string prefix = "[{\"hash\":\"near-capacity\",\"category\":\"bug\",\"description\":\"Capacity boundary fixture\",\"context\":\"";
        const string suffix = "\"}]";
        var original = prefix + new string('x', SuggestionStore.MaxSuggestionStoreBytes - prefix.Length - suffix.Length - 16) + suffix;
        File.WriteAllText(_store.FilePath, original);
        var record = Assert.Single(_store.LoadAll());

        Assert.Throws<IOException>(() => _store.TryLinkIssue(record.Id, record.RevisionHash, "owner/repo", "1", null, null, out _));

        Assert.Equal(original, File.ReadAllText(_store.FilePath));
        Assert.Equal(record.Id, Assert.Single(_store.LoadAll()).Id);
        Assert.False(File.Exists(_store.FilePath + ".bak"));
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempDir), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void TryLinkIssue_FailedAtomicPublicationPreservesHistory_Issue5350()
    {
        var record = MakeRecord("bug", null, "Existing history survives a failed association write");
        Assert.True(_store.TryAdd(record));
        var before = File.ReadAllBytes(_store.FilePath);
        _store.ValidateAssociationWriteForTesting = stagedPath =>
        {
            Assert.Contains("manual_external", File.ReadAllText(stagedPath));
            Assert.Equal(before, File.ReadAllBytes(_store.FilePath));
            throw new IOException("Simulated failure before atomic replacement");
        };
        try
        {
            Assert.Throws<IOException>(() => _store.TryLinkIssue(record.Id, record.RevisionHash, "owner/repo", "1", null, null, out _));
            Assert.Equal(before, File.ReadAllBytes(_store.FilePath));
            Assert.Null(Assert.Single(_store.LoadAll()).UpstreamAssociation);
            Assert.DoesNotContain(Directory.EnumerateFiles(_tempDir), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            _store.ValidateAssociationWriteForTesting = null;
        }
        Assert.Equal(SuggestionStore.MutationResult.Success,
            _store.TryLinkIssue(record.Id, record.RevisionHash, "owner/repo", "1", null, null, out _));
    }

    [Fact]
    public async Task TryLinkIssue_RejectsInFlightSubmissionAndStaleRevision_Issue5350()
    {
        var record = MakeRecord("bug", null, "Submission and association serialize on the same store lock");
        Assert.True(_store.TryAdd(record));
        var replacement = Assert.Single(_store.LoadAll());
        replacement.Context = "A newer edit";
        Assert.Equal(SuggestionStore.MutationResult.Success, _store.TryUpdate(record.Id, record.RevisionHash, replacement, out _));
        Assert.Equal(SuggestionStore.MutationResult.RevisionConflict,
            _store.TryLinkIssue(record.Id, record.RevisionHash, "owner/repo", "1", null, null, out _));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submission = _store.TryAddAndSubmitAsync(record, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return SuggestionStore.SubmitAttemptResult.Failure("Offline");
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var current = Assert.Single(_store.LoadAll());
            Assert.Equal(SuggestionStore.MutationResult.SubmissionInFlight,
                _store.TryLinkIssue(current.Id, current.RevisionHash, "owner/repo", "1", null, null, out _));
        }
        finally
        {
            release.TrySetResult();
            await submission.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var retry = Assert.Single(_store.LoadAll());
        Assert.Equal(SuggestionStore.MutationResult.Success,
            _store.TryLinkIssue(retry.Id, retry.RevisionHash, "owner/repo", "1", null, null, out _));
    }
}
