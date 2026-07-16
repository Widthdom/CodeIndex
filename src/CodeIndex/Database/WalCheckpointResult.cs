namespace CodeIndex.Database;

/// <summary>
/// Structured outcome from one SQLite WAL checkpoint request. SQLite returns
/// <c>(busy, log, checkpointed)</c>; the remaining count is derived so callers do
/// not have to repeat the success calculation.
/// SQLite WAL checkpoint 1 回分の構造化結果。SQLite の
/// <c>(busy, log, checkpointed)</c> と未 checkpoint page 数を保持する。
/// </summary>
public sealed record WalCheckpointResult(
    bool Attempted,
    bool Succeeded,
    long? Busy,
    long? LogPageCount,
    long? CheckpointedPageCount,
    long? RemainingPageCount,
    string? SkippedReason,
    string? FailureReason)
{
    public const string BusyFailureReason = "checkpoint_busy";
    public const string PagesRemainingFailureReason = "checkpoint_pages_remaining";
    public const string MissingResultFailureReason = "checkpoint_result_missing";
    public const string InvalidResultFailureReason = "checkpoint_result_invalid";
    public const string ReadOnlySkippedReason = "read_only_connection";
    public const string CancelledFailureReason = "checkpoint_cancelled";
    public const string GenericFailureReason = "wal_checkpoint_failed";

    internal static WalCheckpointResult NotAttempted(string? skippedReason = null)
        => new(false, false, null, null, null, null, skippedReason, null);

    internal static WalCheckpointResult SkippedAfterAttempt(string skippedReason)
        => new(true, false, null, null, null, null, skippedReason, null);

    internal static WalCheckpointResult Failed(string failureReason)
        => new(true, false, null, null, null, null, null, failureReason);
}
