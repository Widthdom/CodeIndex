using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static partial class IndexWatchRunner
{
    private static string FormatHumanSummary(string status, int? batchSize, long elapsedMs, string subRunJson, int exitCode)
    {
        var prefix = status switch
        {
            "rescanned" => "[watch] rescanned",
            "failed" => "[watch] failed",
            _ => "[watch] updated",
        };
        var batchLabel = batchSize is int n
            ? $" {ConsoleUi.Counted(n, "path", format: "N0")}"
            : string.Empty;

        // Best-effort parse of the sub-run JSON to surface updated/removed/errors counts.
        // The summary is informational; a parse failure must not break the watch loop.
        // サブ実行 JSON から件数を best-effort で抽出。失敗してもループは続行する。
        var details = new List<string>
        {
            $"exit code {exitCode.ToString(CultureInfo.InvariantCulture)}",
        };
        var summary = ParseSubRunSummary(subRunJson);
        if (summary.ParseStatus == "parsed")
        {
            details.Add($"updated {summary.Updated.GetValueOrDefault()}");
            details.Add($"removed {summary.Removed.GetValueOrDefault()}");
            details.Add($"errors {summary.Errors.GetValueOrDefault()}");
            if (string.Equals(status, "rescanned", StringComparison.Ordinal))
            {
                if (summary.FilesScanned is int filesScanned)
                    details.Add($"scanned {filesScanned}");
                if (summary.FilesSkipped is int filesSkipped)
                    details.Add($"skipped {filesSkipped}");
                if (summary.FilesPurged is int filesPurged)
                    details.Add($"purged {filesPurged}");
            }
        }

        var detail = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
        return $"{prefix}{batchLabel}{detail} in {elapsedMs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} ms";
    }

    private static WatchSubRunSummary ParseSubRunSummary(string subRunJson)
    {
        var trimmedLength = TrimTrailingLineBreaks(subRunJson);
        if (trimmedLength == 0)
            return WatchSubRunSummary.Unparsed("missing", "sub-run emitted no JSON");

        if (trimmedLength > MaxHumanSummarySubRunJsonChars)
            return WatchSubRunSummary.Unparsed("too_large", $"sub-run JSON exceeded {MaxHumanSummarySubRunJsonChars.ToString(CultureInfo.InvariantCulture)} characters");

        try
        {
            using var doc = BoundedJson.ParseDocument(
                subRunJson[..trimmedLength],
                MaxHumanSummarySubRunJsonChars * 4,
                MaxHumanSummaryJsonDepth);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("summary", out var summary)
                || summary.ValueKind != JsonValueKind.Object)
            {
                return WatchSubRunSummary.Unparsed("missing_summary", "sub-run JSON did not contain an object summary");
            }

            return new WatchSubRunSummary(
                TryReadInt32(summary, "updated") ?? 0,
                TryReadInt32(summary, "removed") ?? 0,
                TryReadInt32(summary, "errors") ?? 0,
                TryReadInt64(summary, "files_total"),
                TryReadInt32(summary, "files_scanned"),
                TryReadInt32(summary, "files_skipped") ?? TryReadInt32(summary, "skipped"),
                TryReadInt32(summary, "files_purged"),
                TryReadInt32(summary, "warnings"),
                "parsed",
                null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return WatchSubRunSummary.Unparsed("invalid_json", CommandErrorWriter.FormatSanitizedExceptionMessage(ex));
        }
    }

    private static int? TryReadInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? TryReadInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static List<string> BuildBatchPathSamples(string projectRoot, IReadOnlyList<string>? batchPaths, out bool truncated)
    {
        truncated = false;
        if (batchPaths == null || batchPaths.Count == 0)
            return [];

        truncated = batchPaths.Count > BatchPathSampleLimit;
        var samples = new List<string>(Math.Min(batchPaths.Count, BatchPathSampleLimit));
        foreach (var path in batchPaths.Take(BatchPathSampleLimit))
        {
            var sample = path;
            if (Path.IsPathRooted(path))
                sample = FileIndexer.GetRelativePathFromDirectory(projectRoot, path);
            sample = FileIndexer.NormalizePathSeparators(sample);
            var sanitized = DiagnosticRedactor.RedactSensitiveText(sample, "[redacted]", redactPaths: false);
            var bounded = BoundWatchDisplayText(sanitized, BatchPathSampleMaxChars, out var sampleTruncated);
            truncated |= sampleTruncated;
            samples.Add(bounded);
        }

        return samples;
    }

    internal static string? FormatWatchDiagnosticText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var redacted = DiagnosticRedactor.RedactSensitiveText(value, "[redacted]", redactPaths: true);
        return BoundWatchDisplayText(redacted, MaxWatchDiagnosticChars, out _);
    }

    private static string BoundWatchDisplayText(string value, int maxChars, out bool truncated)
    {
        if (maxChars < 0)
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "Watch diagnostic limit must be non-negative.");

        var flattened = FlattenWatchDiagnosticControlChars(value);
        if (flattened.Length <= maxChars)
        {
            truncated = false;
            return flattened;
        }

        truncated = true;
        if (maxChars == 0)
            return string.Empty;

        if (maxChars <= WatchDiagnosticTruncationMarker.Length)
            return WatchDiagnosticTruncationMarker[..maxChars];

        return flattened[..(maxChars - WatchDiagnosticTruncationMarker.Length)] + WatchDiagnosticTruncationMarker;
    }

    private static string FlattenWatchDiagnosticControlChars(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString();
    }

    private static int TrimTrailingLineBreaks(string value)
    {
        var length = value.Length;
        while (length > 0 && (value[length - 1] == '\r' || value[length - 1] == '\n'))
            length--;

        return length;
    }

    private static void EmitWatchStarted(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string projectRoot,
        string resolvedDbPath,
        TimeSpan debounce,
        int maxPendingPaths,
        bool ignoreCase,
        string? backend,
        string? recoveryReason)
    {
        var safeBackend = FormatWatchDiagnosticText(backend) ?? "filesystem_watcher";
        var safeRecoveryReason = FormatWatchDiagnosticText(recoveryReason);
        if (baseOptions.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchStartedJsonResult
            {
                Status = "watching",
                Phase = "initial_scan",
                ProjectRoot = "[redacted]",
                Db = "[redacted]",
                DebounceMs = (int)debounce.TotalMilliseconds,
                WatchPendingPathLimit = maxPendingPaths,
                Backend = safeBackend,
                RecoveryReason = safeRecoveryReason,
                WatchContract = BuildWatchContract(debounce, maxPendingPaths, ignoreCase),
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchStartedJsonResult));
        }
        else
        {
            var recovery = safeRecoveryReason ?? "none";
            CommandErrorWriter.WriteStderr();
            CommandErrorWriter.WriteStderr($"[watch] Watching {projectRoot} for changes (backend {safeBackend}, recovery {recovery}, debounce {(int)debounce.TotalMilliseconds} ms, pending path limit {maxPendingPaths.ToString("N0", CultureInfo.InvariantCulture)}). Press Ctrl+C to stop.");
        }
    }

    private static IndexWatchContractJsonResult BuildWatchContract(
        TimeSpan debounce,
        int maxPendingPaths,
        bool ignoreCase)
        => new()
        {
            Debounce = "quiet_window",
            DebounceMs = (int)debounce.TotalMilliseconds,
            MaxDebounceMs = IndexWatchRunner.MaxDebounceMs,
            PollIntervalMs = IndexWatchRunner.PollIntervalMs,
            WatchPendingPathLimit = maxPendingPaths,
            PathComparison = ignoreCase ? "ordinal_ignore_case" : "ordinal",
            ChangeCoalescing = "distinct_paths_refresh_debounce",
            RenameEvents = "old_and_new_paths",
            OverflowRecovery = "full_rescan_after_debounce",
            WatcherErrorRecovery = "full_rescan_after_debounce",
            BaselineScan = "single_after_backend_start",
            BackendStartRecovery = "fallback_preserve_baseline",
            Cancellation = "cancel_active_sub_run_then_emit_stopped",
            SubRunOutput = "json_quiet_sub_runs",
            McpWatchMode = "unsupported",
        };

    private static void EmitWatchBackendFallback(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string? backend,
        string recoveryReason,
        string? reason,
        bool baselineCompleted = false,
        string phase = "startup")
        => EmitWatchBackendStartupEvent(
            baseOptions,
            jsonOptions,
            status: "backend_fallback",
            backend: backend,
            recoveryReason: recoveryReason,
            reason: reason,
            phase: phase,
            humanAction: phase == "startup"
                ? baselineCompleted
                    ? "switching backend without repeating the baseline; one recovery scan will reconcile the handoff"
                    : "switching backend before the baseline scan"
                : "switching backend; one recovery scan will reconcile the handoff");

    private static void EmitWatchBackendFailure(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string? backend,
        string recoveryReason,
        string? reason,
        string phase = "startup")
        => EmitWatchBackendStartupEvent(
            baseOptions,
            jsonOptions,
            status: "failed",
            backend: backend,
            recoveryReason: recoveryReason,
            reason: reason,
            phase: phase,
            humanAction: phase == "startup"
                ? "watch startup stopped before the baseline scan"
                : "watch stopped because no further backend fallback is available");

    private static void EmitWatchBackendStartupEvent(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string status,
        string? backend,
        string recoveryReason,
        string? reason,
        string phase,
        string humanAction)
    {
        var safeBackend = FormatWatchDiagnosticText(backend) ?? "filesystem_watcher";
        var safeRecoveryReason = FormatWatchDiagnosticText(recoveryReason) ?? "backend_start_failed";
        var safeReason = FormatWatchDiagnosticText(reason);
        if (baseOptions.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = status,
                Phase = phase,
                Backend = safeBackend,
                RecoveryReason = safeRecoveryReason,
                Reason = safeReason,
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult));
            return;
        }

        var detail = string.IsNullOrEmpty(safeReason) ? string.Empty : $"; {safeReason}";
        var failureDescription = phase == "startup"
            ? "startup failed"
            : "reported a fatal error";
        CommandErrorWriter.WriteStderr(
            $"[watch] Backend {safeBackend} {failureDescription} "
            + $"(recovery {safeRecoveryReason}{detail}); {humanAction}.");
    }

    private static void EmitWatchOverflow(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string? reason,
        string resolvedDbPath,
        string phase,
        string? backend,
        string? recoveryReason)
    {
        var safeBackend = FormatWatchDiagnosticText(backend) ?? "filesystem_watcher";
        var safeRecoveryReason = FormatWatchDiagnosticText(recoveryReason) ?? "watcher_error";
        var safeReason = FormatWatchDiagnosticText(reason);
        if (baseOptions.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = "overflow",
                Reason = safeReason,
                Phase = phase,
                Backend = safeBackend,
                RecoveryReason = safeRecoveryReason,
                OverflowReason = safeReason,
                WatchPendingPathLimit = baseOptions.WatchPendingPathLimit,
                RecoveryCommand = BuildOverflowRecoveryCommand(baseOptions, resolvedDbPath, redactPaths: true),
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult));
        }
        else
        {
            var detail = string.IsNullOrEmpty(safeReason) ? string.Empty : $" ({safeReason})";
            CommandErrorWriter.WriteStderr(
                $"[watch] Watcher backend {safeBackend} requires a full rescan "
                + $"(recovery {safeRecoveryReason}){detail}.");
        }
    }

    private static void EmitWatchStopped(IndexCommandOptions baseOptions, JsonSerializerOptions jsonOptions)
    {
        if (baseOptions.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = "stopped",
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult));
        }
        else
        {
            CommandErrorWriter.WriteStderr("[watch] Stopped.");
        }
    }

    private static IndexWatchRecoveryCommandJsonResult BuildOverflowRecoveryCommand(IndexCommandOptions baseOptions, string resolvedDbPath, bool redactPaths = false)
    {
        var args = BuildSubRunArgs(baseOptions, resolvedDbPath);
        args.Insert(0, "index");
        if (redactPaths)
            RedactOverflowRecoveryPathArgs(args);
        return new IndexWatchRecoveryCommandJsonResult
        {
            Command = "cdidx",
            Args = args,
        };
    }

    private static void RedactOverflowRecoveryPathArgs(List<string> args)
    {
        if (args.Count > 1)
            args[1] = "[redacted]";

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--db", StringComparison.Ordinal))
                args[i + 1] = "[redacted]";
        }
    }

    private readonly record struct WatchSubRunSummary(
        int? Updated,
        int? Removed,
        int? Errors,
        long? FilesTotal,
        int? FilesScanned,
        int? FilesSkipped,
        int? FilesPurged,
        int? Warnings,
        string ParseStatus,
        string? ParseReason)
    {
        internal static WatchSubRunSummary Unparsed(string parseStatus, string parseReason)
            => new(null, null, null, null, null, null, null, null, parseStatus, parseReason);
    }
}
