namespace CodeIndex.Database;

internal enum StatusFreshnessState
{
    Fresh,
    Stale,
    Unknown,
    HeadChanged,
    ClockSkew,
}

internal readonly record struct StatusFreshnessEvaluation(
    StatusFreshnessState State,
    string Reason)
{
    public string SummaryLabel => State switch
    {
        StatusFreshnessState.Fresh => "fresh",
        StatusFreshnessState.Stale or StatusFreshnessState.HeadChanged => "stale",
        _ => "unknown",
    };
}

/// <summary>
/// Classifies status freshness from either an authoritative workspace check or
/// the bounded metadata available to ordinary status.
/// authoritative な workspace check、または通常 status で利用できる bounded metadata から
/// freshness を分類する。
/// </summary>
internal static class StatusFreshnessEvaluator
{
    public static StatusFreshnessEvaluation Evaluate(StatusResult status, DateTime utcNow)
    {
        if (status.WorkspaceCheck is not null)
            return Evaluate(status.WorkspaceCheck, status.WorktreeHeadChanged);

        if (status.WorktreeHeadChanged == true)
            return new(StatusFreshnessState.HeadChanged, "head_changed");

        if (status.GitIsDirty == true)
            return new(StatusFreshnessState.Unknown, "worktree_dirty");

        if (!status.IndexedAt.HasValue || !status.LatestModified.HasValue)
            return new(StatusFreshnessState.Unknown, "timestamps_unavailable");

        var indexedAt = NormalizeUtc(status.IndexedAt.Value);
        var latestModified = NormalizeUtc(status.LatestModified.Value);
        var lastWorkspaceFreshenedAt = status.LastWorkspaceFreshenedAt.HasValue
            ? NormalizeUtc(status.LastWorkspaceFreshenedAt.Value)
            : (DateTime?)null;
        var normalizedNow = NormalizeUtc(utcNow);

        if (indexedAt > normalizedNow
            || latestModified > normalizedNow
            || (lastWorkspaceFreshenedAt.HasValue && lastWorkspaceFreshenedAt.Value > normalizedNow))
        {
            return new(StatusFreshnessState.ClockSkew, "timestamp_in_future");
        }

        if (indexedAt >= latestModified)
            return new(StatusFreshnessState.Fresh, "indexed_after_latest_modified");

        if (!lastWorkspaceFreshenedAt.HasValue
            || lastWorkspaceFreshenedAt.Value < latestModified)
        {
            return new(StatusFreshnessState.Stale, "latest_modified_after_freshness_evidence");
        }

        return HasTrustedFresheningContext(status)
            ? new(StatusFreshnessState.Fresh, "workspace_freshened_after_latest_modified")
            : new(StatusFreshnessState.Unknown, "workspace_freshening_unverified");
    }

    public static StatusFreshnessEvaluation Evaluate(IndexFreshnessCheckResult check)
        => Evaluate(check, worktreeHeadChanged: null);

    public static StatusFreshnessEvaluation Evaluate(
        IndexFreshnessCheckResult check,
        bool? worktreeHeadChanged)
    {
        if (worktreeHeadChanged == true)
            return new(StatusFreshnessState.HeadChanged, "head_changed");
        if (!check.Checked)
            return new(StatusFreshnessState.Unknown, "freshness_check_unavailable");
        if (check.MatchesWorkspace)
            return new(StatusFreshnessState.Fresh, check.Reason);
        if (check.HeadChanged || string.Equals(check.Reason, "head_changed", StringComparison.Ordinal))
            return new(StatusFreshnessState.HeadChanged, check.Reason);
        return new(StatusFreshnessState.Stale, check.Reason);
    }

    private static bool HasTrustedFresheningContext(StatusResult status)
    {
        if (status.WorktreeHeadChanged != false
            || status.GitIsDirty != false
            || status.GitIndexMayHideWorktreeChanges != false)
        {
            return false;
        }

        var runtimeHead = NullIfWhiteSpace(status.GitHead);
        var workspaceVerifiedHead = NullIfWhiteSpace(status.WorkspaceVerifiedHeadSha);
        var latestIndexHead = NullIfWhiteSpace(status.IndexedHeadSha);
        return runtimeHead is not null
            && workspaceVerifiedHead is not null
            && latestIndexHead is not null
            && string.Equals(runtimeHead, workspaceVerifiedHead, StringComparison.OrdinalIgnoreCase)
            && string.Equals(workspaceVerifiedHead, latestIndexHead, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
