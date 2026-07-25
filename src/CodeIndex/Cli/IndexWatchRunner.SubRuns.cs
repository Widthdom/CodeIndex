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
    private static List<string> BuildSubRunArgs(IndexCommandOptions baseOptions, string? resolvedDbPath = null)
    {
        // Always pass --json so sub-runs produce a single JSON-line summary on stdout. The
        // watch loop then either forwards that line (user --json) or extracts a one-line
        // human summary (user non-JSON). Otherwise each sub-run would reprint the banner.
        // 常に --json を付けてサブ実行の stdout を1行 JSON に揃える。watch ループ側で
        // 透過 or 整形してから出力する。
        var args = new List<string>(8) { baseOptions.ProjectPath!, "--json", "--quiet" };
        var dbPath = string.IsNullOrEmpty(resolvedDbPath) ? baseOptions.DbPath : resolvedDbPath;
        if (!string.IsNullOrEmpty(dbPath))
        {
            args.Add("--db");
            args.Add(dbPath!);
        }
        if (baseOptions.Verbose && baseOptions.Json)
            args.Add("--verbose");
        if (baseOptions.MaxFileSizeBytes is { } maxFileSizeBytes)
        {
            args.Add("--max-file-bytes");
            args.Add(maxFileSizeBytes.ToString(CultureInfo.InvariantCulture));
        }
        if (baseOptions.MaxSymbolsPerFile != IndexCommandRunner.DefaultMaxSymbolsPerFile)
        {
            args.Add("--max-symbols-per-file");
            args.Add(baseOptions.MaxSymbolsPerFile.ToString(CultureInfo.InvariantCulture));
        }
        if (baseOptions.MaxReferencesPerFile != IndexCommandRunner.DefaultMaxReferencesPerFile)
        {
            args.Add("--max-references-per-file");
            args.Add(baseOptions.MaxReferencesPerFile.ToString(CultureInfo.InvariantCulture));
        }
        if (baseOptions.Parallelism != IndexCommandRunner.DefaultIndexParallelism())
        {
            args.Add("--parallelism");
            args.Add(baseOptions.Parallelism.ToString(CultureInfo.InvariantCulture));
        }
        if (baseOptions.SymlinkPolicy != FileIndexer.SymlinkPolicy.None)
        {
            args.Add("--follow-symlinks");
            args.Add(baseOptions.SymlinkPolicy.ToString().ToLowerInvariant());
        }
        if (baseOptions.SymbolKindFilter.Include.Count > 0)
        {
            args.Add("--include-symbol-kind");
            args.Add(string.Join(",", baseOptions.SymbolKindFilter.Include));
        }
        if (baseOptions.SymbolKindFilter.Exclude.Count > 0)
        {
            args.Add("--exclude-symbol-kind");
            args.Add(string.Join(",", baseOptions.SymbolKindFilter.Exclude));
        }
        return args;
    }

    internal static int InvokeSubRunAndEmit(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        List<string> args,
        Stopwatch stopwatch,
        string status,
        int? batchSize,
        string phase,
        IReadOnlyList<string>? batchPaths,
        CancellationToken cancellationToken)
    {
        string capturedJson;
        string? spoolPath = null;
        int subRunExitCode;
        WatchSubRunCaptureWriter? captureWriter = null;
        try
        {
            TextWriter? spoolWriter = null;
            if (baseOptions.Json)
            {
                spoolPath = Path.Combine(Path.GetTempPath(), $"cdidx-watch-subrun-{Guid.NewGuid():N}.jsonl");
                spoolWriter = new StreamWriter(
                    CreateSubRunSpoolFileStream(spoolPath),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            captureWriter = new WatchSubRunCaptureWriter(MaxHumanSummarySubRunJsonChars + 1, spoolWriter);
            using var subRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            subRunExitCode = IndexCommandRunner.Run(args.ToArray(), jsonOptions, subRunCancellation, captureWriter);

            captureWriter.Flush();
            capturedJson = captureWriter.CapturedText;
        }
        finally
        {
            captureWriter?.Dispose();
        }
        stopwatch.Stop();
        var eventStatus = subRunExitCode == CommandExitCodes.Success ? status : "failed";
        var failureReason = subRunExitCode == CommandExitCodes.Success
            ? null
            : $"{status} sub-run exited with code {subRunExitCode.ToString(CultureInfo.InvariantCulture)}";
        var summary = ParseSubRunSummary(capturedJson);

        if (baseOptions.Json)
        {
            var pathSamples = BuildBatchPathSamples(baseOptions.ProjectPath!, batchPaths, out var pathSamplesTruncated);
            // Pre-pend a watch-event header line so MCP clients can distinguish watch
            // batches from the initial scan. The underlying sub-run result follows.
            // watch バッチであることを示すヘッダ行を先頭に流し、その後にサブ実行 JSON を出す。
            var watchEvent = new IndexWatchEventJsonResult
            {
                Status = eventStatus,
                Phase = phase,
                BatchSize = batchSize,
                BatchPathSamples = pathSamples.Count > 0 ? pathSamples : null,
                BatchPathSampleLimit = batchPaths == null ? null : BatchPathSampleLimit,
                BatchPathSamplesTruncated = batchPaths == null ? null : pathSamplesTruncated,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                ExitCode = subRunExitCode,
                Updated = summary.Updated,
                Removed = summary.Removed,
                Errors = summary.Errors,
                SubRunParseStatus = summary.ParseStatus,
                SubRunParseReason = summary.ParseReason,
                Reason = failureReason,
            };
            var payload = JsonSerializer
                .SerializeToNode(watchEvent, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult)!
                .AsObject();
            AddWatchSubRunSummaryFields(payload, status, subRunExitCode, summary);
            Console.Out.WriteLine(payload.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions)));

            if (!TryWriteSpooledSubRunOutput(spoolPath, out var endedWithLineBreak))
            {
                var trimmed = capturedJson.TrimEnd('\r', '\n');
                if (!string.IsNullOrEmpty(trimmed))
                    Console.Out.WriteLine(trimmed);
            }
            else if (!endedWithLineBreak)
            {
                Console.Out.WriteLine();
            }
        }
        else
        {
            var human = FormatHumanSummary(eventStatus, batchSize, stopwatch.ElapsedMilliseconds, capturedJson, subRunExitCode);
            CommandErrorWriter.WriteStderr(human);
        }

        DeleteSpoolFile(spoolPath);
        return subRunExitCode;
    }

    internal static FileStream CreateSubRunSpoolFileStream(string spoolPath)
        => DataDirectorySecurity.OpenPrivateFileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

    private static bool TryWriteSpooledSubRunOutput(string? spoolPath, out bool endedWithLineBreak)
    {
        endedWithLineBreak = true;
        if (string.IsNullOrWhiteSpace(spoolPath) || !File.Exists(spoolPath) || new FileInfo(spoolPath).Length == 0)
            return false;

        var buffer = new char[8192];
        var wroteAny = false;
        char lastChar = '\0';
        using var reader = new StreamReader(spoolPath, Encoding.UTF8);
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            Console.Out.Write(buffer, 0, read);
            wroteAny = true;
            lastChar = buffer[read - 1];
        }

        endedWithLineBreak = !wroteAny || lastChar is '\n' or '\r';
        return wroteAny;
    }

    private static bool DeleteSpoolFile(string? spoolPath, Action<string>? deleteOverride = null)
    {
        if (string.IsNullOrWhiteSpace(spoolPath))
            return false;

        return AtomicFileWriter.TryDeleteFile(
            spoolPath,
            ex => ReportSpoolCleanupFailure(spoolPath, ex),
            deleteOverride);
    }

    private static void ReportSpoolCleanupFailure(string spoolPath, Exception exception)
    {
        var target = FormatSpoolCleanupTarget(spoolPath);
        var reason = CommandErrorWriter.FormatSanitizedException(exception);
        GlobalToolLog.Error($"watch_spool_cleanup_failed target={target} reason={reason}");
        CommandErrorWriter.WriteStderr(
            $"Warning [watch_spool_cleanup_failed]: failed to delete watch spool file {target} ({reason}).");
    }

    private static string FormatSpoolCleanupTarget(string path)
    {
        try
        {
            var target = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return ConsoleUi.FormatBoundedValue(string.IsNullOrWhiteSpace(target) ? "<spool>" : target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return "<invalid>";
        }
    }

    internal sealed class WatchSubRunCaptureWriter : TextWriter
    {
        private readonly int _maxCapturedChars;
        private readonly TextWriter? _inner;
        private readonly StringBuilder _captured = new();

        internal WatchSubRunCaptureWriter(int maxCapturedChars, TextWriter? inner)
        {
            _maxCapturedChars = Math.Max(0, maxCapturedChars);
            _inner = inner;
        }

        public override Encoding Encoding => _inner?.Encoding ?? Encoding.UTF8;

        internal string CapturedText => _captured.ToString();

        internal bool Truncated { get; private set; }

        public override void Write(char value)
        {
            Capture(stackalloc char[] { value });
            _inner?.Write(value);
        }

        public override void Write(string? value)
        {
            if (value != null)
                Capture(value.AsSpan());
            _inner?.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Capture(buffer.AsSpan(index, count));
            _inner?.Write(buffer, index, count);
        }

        public override void Flush()
        {
            _inner?.Flush();
            base.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner?.Dispose();
            base.Dispose(disposing);
        }

        private void Capture(ReadOnlySpan<char> value)
        {
            var remaining = _maxCapturedChars - _captured.Length;
            if (remaining <= 0)
            {
                if (value.Length > 0)
                    Truncated = true;
                return;
            }

            var take = Math.Min(remaining, value.Length);
            _captured.Append(value[..take]);
            if (take < value.Length)
                Truncated = true;
        }
    }


    private static void AddWatchSubRunSummaryFields(JsonObject payload, string requestedStatus, int subRunExitCode, WatchSubRunSummary summary)
    {
        if (!string.Equals(requestedStatus, "rescanned", StringComparison.Ordinal))
            return;

        payload["rescan_scope"] = "full_workspace";
        payload["rescan_completed"] = subRunExitCode == CommandExitCodes.Success && summary.ParseStatus == "parsed";

        if (summary.FilesTotal is long filesTotal)
            payload["files_total"] = filesTotal;
        if (summary.FilesScanned is int filesScanned)
            payload["files_scanned"] = filesScanned;
        if (summary.FilesSkipped is int filesSkipped)
        {
            payload["files_skipped"] = filesSkipped;
            if (filesSkipped > 0)
                payload["files_skipped_category"] = "unchanged_or_reused_files";
        }
        if (summary.FilesPurged is int filesPurged)
            payload["files_purged"] = filesPurged;
        if (summary.Warnings is int warnings)
            payload["warnings"] = warnings;
    }

    private static JsonSerializerOptions EnsureJsonNodeSerializerOptions(JsonSerializerOptions jsonOptions)
    {
        if (jsonOptions.TypeInfoResolver != null)
            return jsonOptions;

        return new JsonSerializerOptions(jsonOptions)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
    }

}
