using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static string DescribeLockHolder(IndexLockInfo? holder)
    {
        if (holder == null)
            return string.Empty;
        var startedLocal = holder.StartedAt.ToLocalTime();
        var verification = holder.Verification switch
        {
            IndexLockHolderVerification.Verified => "verified",
            IndexLockHolderVerification.Stale => "stale",
            _ => "unverified",
        };
        return $"PID {holder.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({verification}), started {startedLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture)}";
    }
    private static Dictionary<string, string?> GetHotspotFamilyMetaSnapshot(DbContext db, Func<string, string> keyFactory)
    {
        var languages = FileIndexer.GetHotspotFamilyMarkerLanguages();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var keys = new string[languages.Count];
        for (var i = 0; i < languages.Count; i++)
        {
            var lang = languages[i];
            keys[i] = keyFactory(lang);
            values[lang] = null;
        }

        var metaValues = db.GetMetaStrings(keys);
        for (var i = 0; i < languages.Count; i++)
            values[languages[i]] = metaValues.TryGetValue(keys[i], out var value) ? value : null;

        return values;
    }

    private static IndexMemorySampleJsonResult CaptureMemorySample(string phase, Stopwatch stopwatch)
    {
        var snapshot = ProcessMemorySnapshot.Capture();
        return new IndexMemorySampleJsonResult
        {
            Phase = phase,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            HeapBytes = snapshot.HeapBytes,
            TotalAllocatedBytes = snapshot.TotalAllocatedBytes,
            GcHeapSizeBytes = snapshot.GcHeapSizeBytes,
            FragmentedBytes = snapshot.FragmentedBytes,
            WorkingSetBytes = snapshot.WorkingSetBytes,
            Gen0Collections = snapshot.Gen0Collections,
            Gen1Collections = snapshot.Gen1Collections,
            Gen2Collections = snapshot.Gen2Collections,
        };
    }

    private static IndexMemoryTimelineJsonResult? BuildMemoryTimeline(List<IndexMemorySampleJsonResult> samples)
    {
        if (samples.Count == 0)
            return null;

        return new IndexMemoryTimelineJsonResult
        {
            Samples = samples,
            PeakWorkingSetBytes = samples.Max(static sample => sample.WorkingSetBytes),
            PeakHeapBytes = samples.Max(static sample => sample.HeapBytes),
        };
    }

    private static void WarnIfMemoryThresholdExceeded(IndexMemoryTimelineJsonResult? timeline)
    {
        var rawThreshold = CdidxEnvironment.GetProcessEnvironmentVariable("CDIDX_MEM_WARN_MB");
        if (timeline == null || !long.TryParse(rawThreshold, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var thresholdMb) || thresholdMb <= 0)
            return;

        var peakMb = timeline.PeakWorkingSetBytes / (1024 * 1024);
        if (peakMb >= thresholdMb)
            CommandErrorWriter.WriteStderr($"Warning: cdidx working set reached {peakMb:N0} MB (CDIDX_MEM_WARN_MB={thresholdMb:N0}).");
    }

    private static void StampLastIndexRunMetadata(
        DbWriter writer,
        string mode,
        DateTime startedAtUtc,
        long durationMs,
        long filesScanned,
        long filesSkipped,
        long parseErrors,
        long bytesRead,
        long bytesReadSkippedFileCount,
        long rowsUpserted,
        long rowsDeleted,
        IndexMemoryTimelineJsonResult? memoryTimeline)
        => StampLastIndexRunMetadata(
            writer,
            mode,
            startedAtUtc,
            durationMs,
            filesScanned,
            filesSkipped,
            parseErrors,
            bytesRead,
            bytesReadSkippedFileCount,
            rowsUpserted,
            rowsDeleted,
            memoryTimeline,
            diagnostics: null,
            referenceExtractionCapHits: null);

    private static void StampLastIndexRunMetadata(
        DbWriter writer,
        string mode,
        DateTime startedAtUtc,
        long durationMs,
        long filesScanned,
        long filesSkipped,
        long parseErrors,
        long bytesRead,
        long bytesReadSkippedFileCount,
        long rowsUpserted,
        long rowsDeleted,
        IndexMemoryTimelineJsonResult? memoryTimeline,
        IReadOnlyList<string>? diagnostics,
        ReferenceExtractionCapHitSummary? referenceExtractionCapHits)
    {
        writer.SetMetaValues(
            (DbContext.LastIndexRunModeMetaKey, mode),
            (DbContext.LastIndexRunStartedAtMetaKey, startedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunDurationMsMetaKey, durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunFilesScannedMetaKey, filesScanned.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunFilesSkippedMetaKey, filesSkipped.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunParseErrorsMetaKey, parseErrors.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunBytesReadMetaKey, bytesRead.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunBytesReadSkippedFileCountMetaKey, bytesReadSkippedFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunBytesReadIncompleteMetaKey, (bytesReadSkippedFileCount > 0).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunRowsUpsertedMetaKey, rowsUpserted.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunRowsDeletedMetaKey, rowsDeleted.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunReferenceExtractionCapHitsMetaKey, referenceExtractionCapHits == null
                ? null
                : JsonSerializer.Serialize(referenceExtractionCapHits, StatusMetadataJsonContext.Default.ReferenceExtractionCapHitSummary)),
            (DbContext.LastIndexRunPeakMemoryMbMetaKey, memoryTimeline == null
                ? null
                : (memoryTimeline.PeakWorkingSetBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        StampLastIndexRunDiagnostics(writer, diagnostics);
        writer.MarkIndexComplete();
        writer.ClearLastFailedIndexRunMetadata();
    }

    internal static void StampLastIndexRunDiagnostics(DbWriter writer, IReadOnlyList<string>? diagnostics)
    {
        var total = diagnostics?.Count ?? 0;
        if (total == 0)
        {
            writer.SetMetaValues(
                (DbContext.LastIndexRunDiagnosticsMetaKey, null),
                (DbContext.LastIndexRunDiagnosticCountMetaKey, null),
                (DbContext.LastIndexRunDiagnosticsTruncatedMetaKey, null));
            return;
        }

        var sample = JsonStringListCodec.TakeSerializableSample(
            diagnostics!,
            DbContext.LastIndexRunDiagnosticSampleLimit);
        writer.SetMetaValues(
            (DbContext.LastIndexRunDiagnosticsMetaKey, JsonStringListCodec.Serialize(sample)),
            (DbContext.LastIndexRunDiagnosticCountMetaKey, total.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.LastIndexRunDiagnosticsTruncatedMetaKey, (total > sample.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal static Action<DbWriter, IReadOnlyList<string>>? PlannerStatisticsMaintenanceDiagnosticStampingForTesting
    {
        get => ScopedPlannerStatisticsMaintenanceDiagnosticStampingForTesting.Value;
        set => ScopedPlannerStatisticsMaintenanceDiagnosticStampingForTesting.Value = value;
    }

    internal static bool TryStampPlannerStatisticsMaintenanceDiagnostic(
        DbWriter writer,
        List<string> indexRunDiagnostics,
        DbContext.PlannerStatisticsMaintenanceFailure plannerMaintenanceFailure)
    {
        indexRunDiagnostics.Add(FormatPlannerStatisticsMaintenanceDiagnostic(plannerMaintenanceFailure));
        try
        {
            PlannerStatisticsMaintenanceDiagnosticStampingForTesting?.Invoke(writer, indexRunDiagnostics);
            StampLastIndexRunDiagnostics(writer, indexRunDiagnostics);
            return true;
        }
        catch (Exception ex)
        {
            GlobalToolLog.Error("planner_statistics_maintenance_diagnostic_persist_failed", ex, includeStacks: false);
            return false;
        }
    }

    internal static string FormatIndexRunDiagnostic(string code, Exception ex)
    {
        var raw = $"{code}: {ex.GetType().Name}: {DiagnosticRedactor.FormatExceptionMessage(ex, MaxIndexRunDiagnosticLength)}";
        return raw.Length <= MaxIndexRunDiagnosticLength
            ? raw
            : raw[..MaxIndexRunDiagnosticLength] + "...<truncated>";
    }

    internal static string FormatIndexRunDiagnostic(string code, string? target, Exception ex)
    {
        if (string.IsNullOrWhiteSpace(target))
            return FormatIndexRunDiagnostic(code, ex);

        var raw = $"{code}: {CollapseLineBreaks(target)}: {ex.GetType().Name}: {DiagnosticRedactor.FormatExceptionMessage(ex, MaxIndexRunDiagnosticLength)}";
        return raw.Length <= MaxIndexRunDiagnosticLength
            ? raw
            : raw[..MaxIndexRunDiagnosticLength] + "...<truncated>";
    }

    internal static string FormatPlannerStatisticsMaintenanceDiagnostic(DbContext.PlannerStatisticsMaintenanceFailure failure)
        => FormatIndexRunDiagnostic(
            "planner_statistics_maintenance_failed",
            failure.CommandText,
            failure.Exception);

    private static void RecordIndexRunDiagnostic(List<string>? diagnostics, string code, Exception ex)
    {
        if (diagnostics == null)
            return;

        diagnostics.Add(FormatIndexRunDiagnostic(code, ex));
    }

    private static void RecordIndexRunDiagnostic(List<string>? diagnostics, string code, string? target, Exception ex)
    {
        if (diagnostics == null)
            return;

        diagnostics.Add(FormatIndexRunDiagnostic(code, target, ex));
    }

    private static void TryStampLastFailedIndexRun(
        string dbPath,
        string status,
        string mode,
        DateTime startedAtUtc,
        long durationMs,
        long? filesProcessed,
        long? filesTotal,
        string errorCode,
        string reason,
        bool? progressPersisted = null,
        string? recoveryHint = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath)
            || dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(dbPath))
        {
            return;
        }

        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db);
            writer.SetMetaValues(
                (DbContext.LastFailedIndexRunStatusMetaKey, status),
                (DbContext.LastFailedIndexRunModeMetaKey, mode),
                (DbContext.LastFailedIndexRunStartedAtMetaKey, startedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunDurationMsMetaKey, durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesProcessedMetaKey, filesProcessed?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunFilesTotalMetaKey, filesTotal?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunErrorCodeMetaKey, errorCode),
                (DbContext.LastFailedIndexRunReasonMetaKey, reason),
                (DbContext.LastFailedIndexRunProgressPersistedMetaKey, progressPersisted?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.LastFailedIndexRunRecoveryHintMetaKey, recoveryHint),
                (DbContext.LastFailedIndexRunFileErrorsMetaKey, null));
            if (progressPersisted == true)
                writer.MarkIndexIncomplete(["interrupted_index_run"]);
        }
        catch (Exception ex) when (ex is CodeIndexException or IOException or UnauthorizedAccessException or NotSupportedException or SqliteException)
        {
        }
    }

    internal static FileByteReadSummary MeasureReadableFileBytes(
        IEnumerable<string> paths,
        string? projectRoot = null,
        List<string>? diagnostics = null,
        IReadOnlyDictionary<string, long>? knownFileSizes = null)
        => MeasureReadableFileBytes(paths, static path => path, projectRoot, diagnostics, knownFileSizes);

    internal static FileByteReadSummary MeasureReadableFileBytes(
        IEnumerable<string> paths,
        Func<string, string> pathSelector,
        string? projectRoot = null,
        List<string>? diagnostics = null,
        IReadOnlyDictionary<string, long>? knownFileSizes = null)
    {
        long total = 0;
        long skipped = 0;
        foreach (var sourcePath in paths)
        {
            var path = pathSelector(sourcePath);
            if (knownFileSizes != null && knownFileSizes.TryGetValue(path, out var knownSize))
            {
                total += knownSize;
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                    total += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                skipped++;
                RecordIndexRunDiagnostic(diagnostics, "file_size_bytes_skipped", FormatDiagnosticPath(projectRoot, path), ex);
            }
        }

        return new FileByteReadSummary(total, skipped);
    }

    private static string FormatDiagnosticPath(string? projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return path;

        try
        {
            var relative = FileIndexer.NormalizePathSeparators(FileIndexer.GetRelativePathFromDirectory(projectRoot, path));
            return IsOutsideProjectRoot(relative) ? path : relative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return path;
        }
    }
}
