using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public class StatusFreshnessEvaluatorTests
{
    private static readonly DateTime IndexedAt = new(2030, 1, 2, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ModifiedAt = IndexedAt.AddMinutes(1);
    private static readonly DateTime FreshenedAt = IndexedAt.AddMinutes(2);
    private static readonly DateTime EvaluatedAt = IndexedAt.AddMinutes(3);

    [Fact]
    public void Evaluate_OrdinaryStatusUsesOnlyTrustedNoOpFreshening_Issue5227()
    {
        var status = CreateStatus();

        var trusted = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.Fresh, trusted.State);
        Assert.Equal("workspace_freshened_after_latest_modified", trusted.Reason);
        Assert.Equal("fresh", trusted.SummaryLabel);
        Assert.Contains("index fresh", QueryCommandRunner.BuildStatusSummary(status, EvaluatedAt));

        status.GitIndexMayHideWorktreeChanges = true;
        var hiddenIndexState = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.Unknown, hiddenIndexState.State);
        Assert.Equal("workspace_freshening_unverified", hiddenIndexState.Reason);

        status.GitIndexMayHideWorktreeChanges = false;
        status.WorkspaceVerifiedHeadSha = null;
        var unverified = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.Unknown, unverified.State);
        Assert.Equal("workspace_freshening_unverified", unverified.Reason);
        Assert.Equal("unknown", unverified.SummaryLabel);
    }

    [Fact]
    public void Evaluate_OrdinaryStatusKeepsConservativeTimestampAndWorktreeBoundaries_Issue5227()
    {
        var status = CreateStatus();

        status.LatestModified = FreshenedAt.AddSeconds(1);
        var changedAfterFreshening = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.Stale, changedAfterFreshening.State);
        Assert.Equal("latest_modified_after_freshness_evidence", changedAfterFreshening.Reason);

        status.LatestModified = ModifiedAt;
        status.LastWorkspaceFreshenedAt = null;
        var missingFresheningStamp = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.Stale, missingFresheningStamp.State);

        status.LastWorkspaceFreshenedAt = EvaluatedAt.AddSeconds(1);
        var clockSkew = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.ClockSkew, clockSkew.State);
        Assert.Equal("timestamp_in_future", clockSkew.Reason);
        Assert.Equal("unknown", clockSkew.SummaryLabel);

        status.LastWorkspaceFreshenedAt = FreshenedAt;
        status.WorktreeHeadChanged = true;
        var headChanged = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.HeadChanged, headChanged.State);
        Assert.Equal("head_changed", headChanged.Reason);

        status.WorkspaceCheck = new IndexFreshnessCheckResult
        {
            Checked = true,
            MatchesWorkspace = true,
            Reason = "matched",
        };
        var checkedHeadChanged = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.HeadChanged, checkedHeadChanged.State);
        Assert.Equal("head_changed", checkedHeadChanged.Reason);
        var headFreshness = status.HeadFreshness;
        Assert.NotNull(headFreshness);
        Assert.Equal("head_changed", headFreshness.State);
        Assert.Equal("head_changed", headFreshness.StateReason);
        Assert.False(headFreshness.WorkspaceMatchesIndex);

        status.WorkspaceCheck = null;
        status.WorktreeHeadChanged = false;
        status.GitIsDirty = true;
        var dirty = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.Stale, dirty.State);
        Assert.Equal("worktree_dirty", dirty.Reason);

        status.WorkspaceCheck = new IndexFreshnessCheckResult
        {
            Checked = true,
            MatchesWorkspace = true,
            Reason = "matched",
        };
        var checkedDirty = StatusFreshnessEvaluator.Evaluate(status, EvaluatedAt);
        Assert.Equal(StatusFreshnessState.Stale, checkedDirty.State);
        Assert.Equal("worktree_dirty", checkedDirty.Reason);
        Assert.Equal("stale", status.HeadFreshness?.State);
        Assert.False(status.HeadFreshness?.WorkspaceMatchesIndex);
    }

    [Theory]
    [InlineData(false, false, false, "Unknown", "freshness_check_unavailable")]
    [InlineData(true, true, false, "Fresh", "matched")]
    [InlineData(true, false, false, "Stale", "changed_files")]
    [InlineData(true, false, true, "HeadChanged", "head_changed")]
    public void Evaluate_WorkspaceCheckUsesTheSameClassification(
        bool checkedWorkspace,
        bool matchesWorkspace,
        bool headChanged,
        string expectedState,
        string expectedReason)
    {
        var check = new IndexFreshnessCheckResult
        {
            Checked = checkedWorkspace,
            MatchesWorkspace = matchesWorkspace,
            HeadChanged = headChanged,
            Reason = expectedReason == "freshness_check_unavailable" ? "project_root_unavailable" : expectedReason,
        };

        var evaluation = StatusFreshnessEvaluator.Evaluate(check);

        Assert.Equal(expectedState, evaluation.State.ToString());
        Assert.Equal(expectedReason, evaluation.Reason);
    }

    private static StatusResult CreateStatus() => new()
    {
        Files = 1,
        Symbols = 1,
        References = 0,
        Languages = new Dictionary<string, long>(StringComparer.Ordinal) { ["csharp"] = 1 },
        IndexedAt = IndexedAt,
        LatestModified = ModifiedAt,
        LastWorkspaceFreshenedAt = FreshenedAt,
        GitHead = "0123456789abcdef",
        GitIsDirty = false,
        GitIndexMayHideWorktreeChanges = false,
        IndexedHeadSha = "0123456789abcdef",
        WorkspaceVerifiedHeadSha = "0123456789abcdef",
        WorktreeHeadChanged = false,
    };
}
