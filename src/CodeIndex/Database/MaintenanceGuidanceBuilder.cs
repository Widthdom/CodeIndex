using System.Globalization;
using System.Text.Json.Serialization;

namespace CodeIndex.Database;

public sealed class StatusMaintenanceGuidance
{
    [JsonPropertyName("wal_state")]
    public string WalState { get; set; } = "unknown";
    [JsonPropertyName("wal_threshold_bytes")]
    public long WalThresholdBytes { get; set; }
    [JsonPropertyName("freelist_state")]
    public string FreelistState { get; set; } = "unknown";
    [JsonPropertyName("freelist_ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FreelistRatio { get; set; }
    [JsonPropertyName("freelist_threshold_ratio")]
    public double FreelistThresholdRatio { get; set; }
    [JsonPropertyName("estimated_pages_reclaimable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EstimatedPagesReclaimable { get; set; }
    [JsonPropertyName("estimated_bytes_reclaimable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EstimatedBytesReclaimable { get; set; }
    [JsonPropertyName("auto_vacuum_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AutoVacuumMode { get; set; }
    [JsonPropertyName("auto_vacuum_mode_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoVacuumModeName { get; set; }
    [JsonPropertyName("fts_optimization")]
    public FtsOptimizationRecommendation FtsOptimization { get; set; } = new();
    [JsonPropertyName("recommended_command")]
    public string RecommendedCommand { get; set; } = "none";
    [JsonPropertyName("post_maintenance_follow_up")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PostMaintenanceFollowUp { get; set; }
}

internal readonly record struct MaintenanceMetrics(
    long? PageCount,
    long? FreelistCount,
    long? PageSize,
    long? WalSizeBytes,
    long? DbSizeBytes,
    long? AutoVacuumMode);

public sealed class FtsOptimizationRecommendation
{
    [JsonPropertyName("recommended")]
    public bool Recommended { get; init; }
    [JsonPropertyName("action")]
    public string Action { get; init; } = FtsOptimizationRecommendationEvaluator.NoAction;
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = FtsOptimizationRecommendationEvaluator.IncrementalWriteCountUnavailableReason;
    [JsonPropertyName("threshold_writes")]
    public int ThresholdWrites { get; init; } = DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold;
    [JsonPropertyName("observed_writes")]
    public long ObservedWrites { get; init; }
    [JsonPropertyName("state")]
    public string State { get; init; } = FtsOptimizationRecommendationEvaluator.UnavailableState;
}

internal readonly record struct FtsOptimizationMetrics(
    long? IncrementalWritesSinceOptimize,
    long? PageCount,
    bool SnapshotCurrent);

internal static class FtsOptimizationRecommendationEvaluator
{
    public const string OptimizeAction = "optimize";
    public const string NoAction = "none";
    public const string IncrementalWriteThresholdReachedReason = "incremental_write_threshold_reached";
    public const string IncrementalWriteThresholdNotReachedReason = "incremental_write_threshold_not_reached";
    public const string IncrementalWriteCountUnavailableReason = "incremental_write_count_unavailable";
    public const string PageCountUnavailableReason = "page_count_unavailable";
    public const string MaintenanceSnapshotStaleReason = "maintenance_snapshot_stale";
    public const string CurrentState = "current";
    public const string StaleState = "stale";
    public const string UnavailableState = "unavailable";

    public static FtsOptimizationRecommendation Evaluate(
        FtsOptimizationMetrics metrics,
        int thresholdWrites = DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold)
    {
        if (thresholdWrites <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdWrites));

        var observedWrites = metrics.IncrementalWritesSinceOptimize is >= 0
            ? metrics.IncrementalWritesSinceOptimize.Value
            : 0;

        if (!metrics.SnapshotCurrent)
            return Build(
                recommended: false,
                NoAction,
                MaintenanceSnapshotStaleReason,
                thresholdWrites,
                observedWrites,
                StaleState);

        if (metrics.PageCount is null or <= 0)
            return Build(
                recommended: false,
                NoAction,
                PageCountUnavailableReason,
                thresholdWrites,
                observedWrites,
                UnavailableState);

        if (metrics.IncrementalWritesSinceOptimize is null or < 0)
            return Build(
                recommended: false,
                NoAction,
                IncrementalWriteCountUnavailableReason,
                thresholdWrites,
                observedWrites,
                UnavailableState);

        var recommended = observedWrites >= thresholdWrites;
        return Build(
            recommended,
            recommended ? OptimizeAction : NoAction,
            recommended
                ? IncrementalWriteThresholdReachedReason
                : IncrementalWriteThresholdNotReachedReason,
            thresholdWrites,
            observedWrites,
            CurrentState);
    }

    private static FtsOptimizationRecommendation Build(
        bool recommended,
        string action,
        string reason,
        int thresholdWrites,
        long observedWrites,
        string state) =>
        new()
        {
            Recommended = recommended,
            Action = action,
            Reason = reason,
            ThresholdWrites = thresholdWrites,
            ObservedWrites = observedWrites,
            State = state,
        };
}

internal static class MaintenanceGuidanceBuilder
{
    public const string WalWarnBytesEnvironmentVariable = "CDIDX_MAINTENANCE_WAL_WARN_BYTES";
    public const string FreelistWarnRatioEnvironmentVariable = "CDIDX_MAINTENANCE_FREELIST_WARN_RATIO";
    public const long DefaultWalWarnBytes = 64L * 1024 * 1024;
    public const double DefaultFreelistWarnRatio = 0.20;
    private const int RatioDecimalPlaces = 4;

    public static StatusMaintenanceGuidance Build(
        MaintenanceMetrics metrics,
        string vacuumCommand = "cdidx vacuum --db <db>",
        string checkpointCommand = "sqlite3 <db> \"PRAGMA wal_checkpoint(TRUNCATE);\"",
        string optimizeCommand = "cdidx optimize --db <db>",
        FtsOptimizationRecommendation? ftsOptimization = null)
    {
        var walThresholdBytes = ReadPositiveLongEnvironment(WalWarnBytesEnvironmentVariable, DefaultWalWarnBytes);
        var freelistThresholdRatio = ReadRatioEnvironment(FreelistWarnRatioEnvironmentVariable, DefaultFreelistWarnRatio);
        var freelistRatio = CalculateFreelistRatio(metrics.PageCount, metrics.FreelistCount);
        var estimatedBytes = EstimateBytes(metrics.FreelistCount, metrics.PageSize);
        var walState = metrics.WalSizeBytes.HasValue
            ? metrics.WalSizeBytes.Value >= walThresholdBytes ? "checkpoint_recommended" : "ok"
            : "unknown";
        var freelistState = freelistRatio.HasValue
            ? freelistRatio.Value >= freelistThresholdRatio && metrics.FreelistCount.GetValueOrDefault() > 0
                ? "vacuum_recommended"
                : "ok"
            : "unknown";
        ftsOptimization ??= FtsOptimizationRecommendationEvaluator.Evaluate(
            new FtsOptimizationMetrics(
                IncrementalWritesSinceOptimize: null,
                PageCount: metrics.PageCount,
                SnapshotCurrent: true));

        var recommendedCommand = freelistState == "vacuum_recommended"
            ? vacuumCommand
            : walState == "checkpoint_recommended"
                ? checkpointCommand
                : walState == "ok"
                    && freelistState == "ok"
                    && ftsOptimization.Recommended
                    ? optimizeCommand
                    : "none";

        return new StatusMaintenanceGuidance
        {
            WalState = walState,
            WalThresholdBytes = walThresholdBytes,
            FreelistState = freelistState,
            FreelistRatio = freelistRatio.HasValue ? Math.Round(freelistRatio.Value, RatioDecimalPlaces) : null,
            FreelistThresholdRatio = freelistThresholdRatio,
            EstimatedPagesReclaimable = metrics.FreelistCount,
            EstimatedBytesReclaimable = estimatedBytes,
            AutoVacuumMode = metrics.AutoVacuumMode,
            AutoVacuumModeName = FormatAutoVacuumMode(metrics.AutoVacuumMode),
            FtsOptimization = ftsOptimization,
            RecommendedCommand = recommendedCommand,
            PostMaintenanceFollowUp = BuildFollowUp(walState, freelistState, checkpointCommand),
        };
    }

    public static string? FormatAutoVacuumMode(long? mode) => mode switch
    {
        0 => "none",
        1 => "full",
        2 => "incremental",
        null => null,
        _ => "unknown",
    };

    private static double? CalculateFreelistRatio(long? pageCount, long? freelistCount)
    {
        if (!pageCount.HasValue || !freelistCount.HasValue || pageCount.Value <= 0 || freelistCount.Value < 0)
            return null;
        return (double)freelistCount.Value / pageCount.Value;
    }

    private static long? EstimateBytes(long? pages, long? pageSize)
    {
        if (!pages.HasValue || !pageSize.HasValue || pages.Value < 0 || pageSize.Value <= 0)
            return null;
        if (pages.Value > long.MaxValue / pageSize.Value)
            return null;
        return pages.Value * pageSize.Value;
    }

    private static string? BuildFollowUp(string walState, string freelistState, string checkpointCommand)
    {
        if (freelistState == "vacuum_recommended" && walState == "checkpoint_recommended")
            return $"After vacuum, rerun `cdidx status --json`; if wal_state remains checkpoint_recommended, close active writers and run `{checkpointCommand}`.";
        if (freelistState == "vacuum_recommended")
            return "After vacuum, rerun `cdidx status --json` to confirm freelist_count and wal_size_bytes.";
        if (walState == "checkpoint_recommended")
            return "Close active cdidx writers before checkpointing, then rerun `cdidx status --json` to confirm wal_size_bytes.";
        return null;
    }

    private static long ReadPositiveLongEnvironment(string name, long fallback)
    {
        var value = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(name);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static double ReadRatioEnvironment(string name, double fallback)
    {
        var value = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            && parsed <= 1
                ? parsed
                : fallback;
    }
}
