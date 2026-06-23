using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Best-effort stderr/lifecycle log sink for non-development executions.
/// 開発実行以外向けのベストエフォートな stderr/ライフサイクルログ出力先。
/// </summary>
internal static class GlobalToolLog
{
    private const int RetainedLogFileCount = 30;
    internal const string LogFormatEnvironmentVariable = "CDIDX_LOG_FORMAT";
    internal const string LogRetainEnvironmentVariable = "CDIDX_LOG_RETAIN";
    internal const string LogMaxSizeMbEnvironmentVariable = "CDIDX_LOG_MAX_SIZE_MB";
    internal const string GlobalToolLogMaxBytesEnvironmentVariable = "CDIDX_GLOBAL_TOOL_LOG_MAX_BYTES";
    private const long DefaultLogMaxSizeBytes = 50L * 1024L * 1024L;
    internal const int MirroredStderrWriteMaxChars = 8192;
    internal const int MaxLogSizeMb = 1024;
    internal const long MaxLogSizeBytes = MaxLogSizeMb * 1024L * 1024L;
    internal const int MaxExceptionChainChars = 8192;
    internal const int RedactionArgumentLengthLimit = 8192;
    internal const string RedactionTruncationMarker = "<truncated>";
    internal const string ExceptionChainTruncationMarker = "...<exception_chain_truncated>";
    private const string RedactedValue = "<redacted>";
    private const int PrivateLogDiagnosticEmitLimit = 16;
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    private static readonly AsyncLocal<Session?> CurrentSession = new();

    internal static IDisposable? TryStart(string[] args, string appVersion)
        => TryStart(args, appVersion, createWriter: null, afterWriterCreated: null);

    internal static IDisposable? TryStartForTesting(
        string[] args,
        string appVersion,
        Func<string, StreamWriter>? createWriter = null,
        Action? afterWriterCreated = null)
        => TryStart(args, appVersion, createWriter, afterWriterCreated);

    private static IDisposable? TryStart(
        string[] args,
        string appVersion,
        Func<string, StreamWriter>? createWriter,
        Action? afterWriterCreated)
    {
        StreamWriter? writer = null;
        try
        {
            if (!ShouldEnable())
                return null;

            var privateLogDiagnostics = new List<PrivateLogFileDiagnostic>();
            var logDirectory = ResolveLogDirectory();
            Directory.CreateDirectory(logDirectory);
            HardenLogFiles(logDirectory, privateLogDiagnostics.Add);
            var options = LogOptions.FromEnvironment();
            var logPath = ResolveLogPath(logDirectory, options);
            writer = createWriter?.Invoke(logPath) ?? CreateLogWriter(logPath);
            afterWriterCreated?.Invoke();
            SetLogFilePermissions(logPath, privateLogDiagnostics.Add);
            PruneOldLogs(logDirectory, options.RetainCount, privateLogDiagnostics.Add);

            var session = new Session(writer, logPath, options.Format);
            writer = null;
            CurrentSession.Value = session;
            session.AttachErrorMirror();
            session.Write("INFO", $"session_start pid={Environment.ProcessId} version={appVersion}");
            session.Write("INFO", $"process_path={Environment.ProcessPath ?? "<unknown>"}");
            session.Write("INFO", $"base_dir={AppContext.BaseDirectory}");
            session.Write("INFO", $"cwd={Environment.CurrentDirectory}");
            session.Write("INFO", $"args={FormatArgs(args)}");
            WritePrivateLogDiagnostics(session, privateLogDiagnostics);
            return session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writer?.Dispose();
            CurrentSession.Value = null;
            return null;
        }
    }

    private static StreamWriter CreateLogWriter(string logPath) =>
        PrivateLogFile.OpenAppendText(logPath);

    internal static void Info(string message) => CurrentSession.Value?.Write("INFO", message);

    internal static void Error(string message) => CurrentSession.Value?.Write("ERROR", message);

    internal static void Error(string message, Exception exception, bool includeStacks = true) =>
        CurrentSession.Value?.Write("ERROR", $"{message}\n{FormatExceptionChain(exception, includeStacks)}");

    internal static string FormatExceptionChain(Exception ex, bool includeStacks = false)
    {
        var sb = new StringBuilder();
        AppendException(sb, ex, 0, includeStacks);
        return TruncateExceptionChain(sb.ToString().TrimEnd());
    }

    private static bool AppendException(StringBuilder sb, Exception ex, int depth, bool includeStacks)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent);
        sb.Append(depth == 0 ? "exception" : "inner_exception");
        sb.Append('[');
        sb.Append(depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("] type=");
        sb.Append(ex.GetType().FullName);
        sb.Append(" message=");
        sb.Append(QuoteLogValue(DiagnosticRedactor.ClassifyException(ex)));
        sb.AppendLine();
        if (HasReachedExceptionChainLimit(sb))
            return false;

        if (includeStacks && !string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            foreach (var line in ex.StackTrace.Split('\n'))
            {
                sb.Append(indent);
                sb.Append("  stack: ");
                sb.AppendLine(DiagnosticRedactor.FormatExceptionStackLine(line.TrimEnd('\r')));
                if (HasReachedExceptionChainLimit(sb))
                    return false;
            }
        }

        if (ex is AggregateException aggregate)
        {
            var index = 0;
            foreach (var inner in aggregate.InnerExceptions)
            {
                sb.Append(indent);
                sb.Append("  aggregate_inner_index=");
                sb.AppendLine(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (HasReachedExceptionChainLimit(sb) || !AppendException(sb, inner, depth + 1, includeStacks))
                    return false;
                index++;
            }

            return true;
        }

        if (ex.InnerException is not null)
            return AppendException(sb, ex.InnerException, depth + 1, includeStacks);

        return true;
    }

    private static bool HasReachedExceptionChainLimit(StringBuilder sb) => sb.Length >= MaxExceptionChainChars;

    private static string TruncateExceptionChain(string value)
    {
        if (value.Length <= MaxExceptionChainChars)
            return value;

        if (MaxExceptionChainChars <= ExceptionChainTruncationMarker.Length)
            return ExceptionChainTruncationMarker[..MaxExceptionChainChars];

        return value[..(MaxExceptionChainChars - ExceptionChainTruncationMarker.Length)] + ExceptionChainTruncationMarker;
    }

    private static string QuoteLogValue(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static bool ShouldEnable()
    {
        var disabled = CdidxEnvironment.GetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG");
        if (TryParseEnvBool(disabled, out var disabledValue) && disabledValue)
            return false;

        var forced = CdidxEnvironment.GetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG");
        if (TryParseEnvBool(forced, out var forcedValue) && forcedValue)
            return true;

        return !LooksLikeDevelopmentExecution(AppContext.BaseDirectory)
            && !LooksLikeDevelopmentExecution(Environment.ProcessPath);
    }

    internal static bool TryParseEnvBool(string? raw, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                value = false;
                return true;
            default:
                return false;
        }
    }

    internal static bool LooksLikeDevelopmentExecutionForTesting(string? path) => LooksLikeDevelopmentExecution(path);

    private static bool LooksLikeDevelopmentExecution(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = NormalizePathForDevelopmentDetection(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return ContainsPathSegments(normalized, ["src", "CodeIndex", "bin"], comparison)
            || ContainsPathSegments(normalized, ["tests", "CodeIndex.Tests", "bin"], comparison);
    }

    private static string NormalizePathForDevelopmentDetection(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool ContainsPathSegments(string path, string[] expectedSegments, StringComparison comparison)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < expectedSegments.Length)
            return false;

        for (var start = 0; start <= segments.Length - expectedSegments.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < expectedSegments.Length; offset++)
            {
                if (!string.Equals(segments[start + offset], expectedSegments[offset], comparison))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolve the lifecycle-log directory cdidx writes to, using the same precedence
    /// as <see cref="TryStart"/>. Exposed to `cdidx report` so the bug-report bundle
    /// can locate recent stderr log files without duplicating the platform-fallback
    /// logic.
    /// `cdidx report` 用に <see cref="TryStart"/> と同じ優先順位で
    /// ライフサイクルログのディレクトリ解決を公開する。
    /// </summary>
    internal static string ResolveLogDirectoryForReport() => ResolveLogDirectory();

    internal static string ResolveLogDirectoryForStatus() => ResolveLogDirectory();

    private static string ResolveLogDirectory()
    {
        foreach (var candidate in EnumerateLogDirectoryCandidates())
        {
            if (!TryNormalizeLogDirectoryCandidate(candidate, out var fullPath))
                continue;

            if (CanWriteProbe(fullPath))
                return fullPath;
        }

        return ResolveTempFallbackLogDirectory();
    }

    internal static bool TryNormalizeLogDirectoryCandidate(string candidate, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateLogDirectoryCandidates()
    {
        var overrideDirectory = CdidxEnvironment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            yield return ExpandUserLogDirectory(overrideDirectory);

        var xdgStateHome = CdidxEnvironment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
            yield return Path.Combine(xdgStateHome, "cdidx", "logs");

        var xdgCacheHome = CdidxEnvironment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCacheHome))
            yield return Path.Combine(xdgCacheHome, "cdidx", "logs");

        var xdgRuntimeDir = CdidxEnvironment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDir))
            yield return Path.Combine(xdgRuntimeDir, "cdidx", "logs");

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                yield return Path.Combine(localAppData, "cdidx", "logs");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, "Library", "Logs", "cdidx");

        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".local", "state", "cdidx", "logs");

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(fallback))
            yield return Path.Combine(fallback, "cdidx", "logs");

        yield return ResolveTempFallbackLogDirectory();
    }

    internal static string ResolveTempFallbackLogDirectoryForTesting() => ResolveTempFallbackLogDirectory();

    private static string ResolveTempFallbackLogDirectory()
        => DataDirectorySecurity.ResolveSensitiveTempFallbackDirectory("logs");

    private static bool CanWriteProbe(string directory)
    {
        try
        {
            DataDirectorySecurity.CreateSensitiveDirectory(directory);
            var probePath = Path.Combine(directory, $".cdidx-write-probe-{Guid.NewGuid():N}.tmp");
            return FileWriteProbe.TryWriteAndDeleteEmptyFile(probePath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ExpandUserLogDirectory(string directory)
    {
        var trimmed = directory.Trim();
        if (trimmed == "~")
            return GetHomeDirectoryOrOriginal(trimmed);

        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = GetHomeDirectoryOrOriginal("~");
            return home == "~" ? trimmed : Path.Combine(home, trimmed[2..]);
        }

        if (trimmed == "$HOME" || trimmed == "${HOME}")
            return GetHomeDirectoryOrOriginal(trimmed);

        if (trimmed.StartsWith("$HOME/", StringComparison.Ordinal) || trimmed.StartsWith("$HOME\\", StringComparison.Ordinal))
        {
            var home = GetHomeDirectoryOrOriginal("$HOME");
            return home == "$HOME" ? trimmed : Path.Combine(home, trimmed[6..]);
        }

        if (trimmed.StartsWith("${HOME}/", StringComparison.Ordinal) || trimmed.StartsWith("${HOME}\\", StringComparison.Ordinal))
        {
            var home = GetHomeDirectoryOrOriginal("${HOME}");
            return home == "${HOME}" ? trimmed : Path.Combine(home, trimmed[8..]);
        }

        return trimmed;
    }

    private static string GetHomeDirectoryOrOriginal(string original)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? original : home;
    }

    private static string ResolveLogPath(string logDirectory, LogOptions options)
    {
        var date = TimeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var processSuffix = CreateProcessLogSuffix();
        if (options.MaxSizeBytes <= 0)
            return Path.Combine(logDirectory, $"stderr-{date}-{processSuffix}.log");

        for (var index = 0; index < 10_000; index++)
        {
            var suffix = index == 0 ? "" : $"-{index}";
            var candidate = Path.Combine(logDirectory, $"stderr-{date}-{processSuffix}{suffix}.log");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < options.MaxSizeBytes)
                return candidate;
        }

        return Path.Combine(logDirectory, $"stderr-{date}-{processSuffix}-{Guid.NewGuid():N}.log");
    }

    private static string CreateProcessLogSuffix()
    {
        var startTime = TimeProvider.GetUtcNow().UtcDateTime.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return FormattableString.Invariant($"p{Environment.ProcessId}-{startTime}");
    }

    private static void PruneOldLogs(
        string logDirectory,
        int retainedLogFileCount,
        Action<PrivateLogFileDiagnostic>? diagnosticSink)
    {
        try
        {
            PrivateLogFile.PruneOldFiles(logDirectory, "stderr-*.log", retainedLogFileCount, diagnosticSink);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    private static void HardenLogFiles(string logDirectory, Action<PrivateLogFileDiagnostic>? diagnosticSink)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            PrivateLogFile.HardenExisting(logDirectory, "stderr-*.log", diagnosticSink);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    private static void SetLogFilePermissions(string logPath, Action<PrivateLogFileDiagnostic>? diagnosticSink)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            PrivateLogFile.TrySetPrivatePermissions(logPath, diagnosticSink);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    private static void WritePrivateLogDiagnostics(Session session, IReadOnlyList<PrivateLogFileDiagnostic> diagnostics)
    {
        var emitted = Math.Min(diagnostics.Count, PrivateLogDiagnosticEmitLimit);
        for (var i = 0; i < emitted; i++)
        {
            var diagnostic = diagnostics[i];
            session.Write(
                "WARN",
                "private_log_diagnostic"
                    + $" operation={QuoteLogValue(diagnostic.Operation)}"
                    + $" reason={QuoteLogValue(diagnostic.Reason)}"
                    + $" target={QuoteLogValue(diagnostic.Target)}");
        }

        if (diagnostics.Count > emitted)
        {
            session.Write(
                "WARN",
                "private_log_diagnostics_truncated"
                    + $" omitted={(diagnostics.Count - emitted).ToString(CultureInfo.InvariantCulture)}");
        }
    }

    internal static string FormatArgs(string[] args)
    {
        if (args.Length == 0)
            return "<none>";

        return string.Join(" ", RedactArgs(args).Select(QuoteArg));
    }

    private static IEnumerable<string> RedactArgs(string[] args)
    {
        var mode = CdidxEnvironment.GetEnvironmentVariable("CDIDX_LOG_REDACT");
        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
            return args;

        var full = string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase);
        var redacted = new string[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            var current = RedactSensitiveText(args[i]);
            if (IsSensitiveFlag(args[i]) && i + 1 < args.Length)
            {
                redacted[i] = current;
                redacted[++i] = RedactedValue;
                continue;
            }

            redacted[i] = full ? RedactPathLikeValue(current) : current;
        }

        return redacted;
    }

    private static bool IsSensitiveFlag(string arg)
    {
        if (!arg.StartsWith('-') || arg.Contains('=', StringComparison.Ordinal))
            return false;

        return DiagnosticRedactor.IsSensitiveName(arg);
    }

    private static string RedactSensitiveText(string value)
    {
        var truncated = false;
        if (value.Length > RedactionArgumentLengthLimit)
        {
            value = value[..RedactionArgumentLengthLimit];
            truncated = true;
        }

        try
        {
            value = DiagnosticRedactor.RedactSensitiveText(value, RedactedValue);
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactedValue;
        }

        if (truncated && LooksLikeTruncatedUriUserInfo(value))
            return RedactedValue;
        if (truncated && IsFullyRedactedSensitiveAssignment(value))
            return value;

        return truncated ? value + RedactionTruncationMarker : value;
    }

    private static bool IsFullyRedactedSensitiveAssignment(string value)
    {
        var separator = value.IndexOf('=');
        return separator > 0
            && value[(separator + 1)..].Equals(RedactedValue, StringComparison.Ordinal)
            && DiagnosticRedactor.IsSensitiveName(value[..separator]);
    }

    private static bool LooksLikeTruncatedUriUserInfo(string value)
    {
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return false;

        var authorityStart = schemeEnd + 3;
        if (authorityStart >= value.Length)
            return false;

        var authorityEnd = value.Length;
        for (var i = authorityStart; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsWhiteSpace(ch) || ch == '/' || ch == '?' || ch == '#')
            {
                authorityEnd = i;
                break;
            }
        }

        if (authorityEnd <= authorityStart)
            return false;

        var authorityLength = authorityEnd - authorityStart;
        if (value.IndexOf('@', authorityStart, authorityLength) >= 0)
            return false;

        return value.IndexOf(':', authorityStart, authorityLength) >= 0;
    }

    private static string RedactPathLikeValue(string value)
    {
        if (value.Length < 2 || value.StartsWith("-", StringComparison.Ordinal))
            return value;

        if (!value.Contains("/", StringComparison.Ordinal) && !value.Contains("\\", StringComparison.Ordinal))
            return value;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"<path:{Convert.ToHexString(bytes, 0, 8).ToLowerInvariant()}>";
    }

    private static string QuoteArg(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";

        return arg.Any(char.IsWhiteSpace) ? $"\"{arg.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : arg;
    }

    private sealed class Session : IDisposable
    {
        private readonly object _gate = new();
        private readonly StreamWriter _writer;
        private readonly string _format;
        private TextWriter? _originalError;
        private TextWriter? _teeError;
        private bool _disposed;

        public Session(StreamWriter writer, string logPath, string format)
        {
            _writer = writer;
            _format = format;
            LogPath = logPath;
        }

        public string LogPath { get; }

        public void AttachErrorMirror()
        {
            lock (_gate)
            {
                if (_disposed || _teeError != null)
                    return;

                _originalError = Console.Error;
                _teeError = TextWriter.Synchronized(new TeeTextWriter(_originalError, _writer));
                Console.SetError(_teeError);
            }
        }

        public void Write(string level, string message)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                try
                {
                    if (string.Equals(_format, "json", StringComparison.Ordinal))
                    {
                        _writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            ["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                            ["level"] = level,
                            ["msg"] = message,
                        }));
                    }
                    else
                    {
                        _writer.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] {message}"));
                    }
                    _writer.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Best-effort only / ベストエフォートのみ
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                try
                {
                    if (_originalError != null)
                        Console.SetError(_originalError);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Best-effort only / ベストエフォートのみ
                }

                CurrentSession.Value = null;
                _writer.Dispose();
            }
        }
    }

    internal sealed record LogOptions(string Format, int RetainCount, long MaxSizeBytes)
    {
        public static LogOptions FromEnvironment()
        {
            var format = CdidxEnvironment.GetEnvironmentVariable(LogFormatEnvironmentVariable)?.Trim().ToLowerInvariant();
            if (format is not "json")
                format = "text";

            var retainCount = RetainedLogFileCount;
            if (int.TryParse(CdidxEnvironment.GetEnvironmentVariable(LogRetainEnvironmentVariable), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedRetain))
                retainCount = Math.Clamp(parsedRetain, 1, 10_000);

            var maxSizeBytes = DefaultLogMaxSizeBytes;
            if (int.TryParse(CdidxEnvironment.GetEnvironmentVariable(LogMaxSizeMbEnvironmentVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMb) && parsedMb > 0)
            {
                if (parsedMb <= MaxLogSizeMb)
                    maxSizeBytes = parsedMb * 1024L * 1024L;
            }
            else if (long.TryParse(CdidxEnvironment.GetEnvironmentVariable(GlobalToolLogMaxBytesEnvironmentVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBytes)
                && parsedBytes is > 0 and <= MaxLogSizeBytes)
            {
                maxSizeBytes = parsedBytes;
            }

            return new LogOptions(format, retainCount, maxSizeBytes);
        }
    }

    private sealed class TeeTextWriter(TextWriter primary, TextWriter secondary) : TextWriter
    {
        public override Encoding Encoding => primary.Encoding;

        public override void Flush()
        {
            TryWrite(primary.Flush);
            TryWrite(secondary.Flush);
        }

        public override void Write(char value)
        {
            TryWrite(() => primary.Write(value));
            TryWrite(() => secondary.Write(value));
        }

        public override void Write(string? value)
        {
            TryWrite(() => primary.Write(value));
            TryWrite(() => secondary.Write(FormatMirroredWrite(value)));
        }

        public override void WriteLine(string? value)
        {
            TryWrite(() => primary.WriteLine(value));
            TryWrite(() => secondary.WriteLine(FormatMirroredWrite(value)));
        }

        private static string? FormatMirroredWrite(string? value)
            => value == null ? null : ConsoleUi.FormatBoundedValue(value, MirroredStderrWriteMaxChars);

        private static void TryWrite(Action write)
        {
            try
            {
                write();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Best-effort mirror: a disposed console writer must not cascade into callers.
                // mirror はベストエフォート。閉じた console writer が呼び出し側へ波及しないようにする。
            }
        }
    }
}
