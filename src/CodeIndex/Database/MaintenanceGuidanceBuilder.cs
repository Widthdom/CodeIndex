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
        string checkpointCommand = "sqlite3 <db> \"PRAGMA wal_checkpoint(TRUNCATE);\"")
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

        var recommendedCommand = freelistState == "vacuum_recommended"
            ? vacuumCommand
            : walState == "checkpoint_recommended"
                ? checkpointCommand
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
