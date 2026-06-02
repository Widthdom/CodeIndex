using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

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
    internal const int MaxLogSizeMb = 1024;
    internal const long MaxLogSizeBytes = MaxLogSizeMb * 1024L * 1024L;
    private const string RedactedValue = "<redacted>";
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    private static readonly AsyncLocal<Session?> CurrentSession = new();
    private static readonly Regex SensitiveAssignmentPattern = new(
        @"^(?<name>--?[^=\s]*(?:token|password|passwd|pwd|secret|auth|apikey|api-key|access-key|credential)[^=\s]*)=(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UriUserInfoPattern = new(
        @"(?<scheme>[a-z][a-z0-9+\-.]*://)(?<user>[^:@/\s]+):(?<password>[^@/\s]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LongHexPattern = new(
        @"\b[0-9a-f]{32,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LongBase64Pattern = new(
        @"\b[A-Za-z0-9+/]{40,}={0,2}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

            var logDirectory = ResolveLogDirectory();
            Directory.CreateDirectory(logDirectory);
            HardenLogFiles(logDirectory);
            var options = LogOptions.FromEnvironment();
            var logPath = ResolveLogPath(logDirectory, options);
            writer = createWriter?.Invoke(logPath) ?? CreateLogWriter(logPath);
            afterWriterCreated?.Invoke();
            SetLogFilePermissions(logPath);
            PruneOldLogs(logDirectory, options.RetainCount);

            var session = new Session(writer, logPath, options.Format);
            writer = null;
            CurrentSession.Value = session;
            session.AttachErrorMirror();
            session.Write("INFO", $"session_start pid={Environment.ProcessId} version={appVersion}");
            session.Write("INFO", $"process_path={Environment.ProcessPath ?? "<unknown>"}");
            session.Write("INFO", $"base_dir={AppContext.BaseDirectory}");
            session.Write("INFO", $"cwd={Environment.CurrentDirectory}");
            session.Write("INFO", $"args={FormatArgs(args)}");
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
        return sb.ToString().TrimEnd();
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth, bool includeStacks)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent);
        sb.Append(depth == 0 ? "exception" : "inner_exception");
        sb.Append('[');
        sb.Append(depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("] type=");
        sb.Append(ex.GetType().FullName);
        sb.Append(" message=");
        sb.Append(QuoteLogValue(ex.Message));
        sb.AppendLine();

        if (includeStacks && !string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            foreach (var line in ex.StackTrace.Split('\n'))
            {
                sb.Append(indent);
                sb.Append("  stack: ");
                sb.AppendLine(line.TrimEnd('\r'));
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
                AppendException(sb, inner, depth + 1, includeStacks);
                index++;
            }

            return;
        }

        if (ex.InnerException is not null)
            AppendException(sb, ex.InnerException, depth + 1, includeStacks);
    }

    private static string QuoteLogValue(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static bool ShouldEnable()
    {
        var disabled = Environment.GetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG");
        if (TryParseEnvBool(disabled, out var disabledValue) && disabledValue)
            return false;

        var forced = Environment.GetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG");
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

        var fallback = Path.Combine(Path.GetTempPath(), "cdidx", "logs");
        return Path.GetFullPath(fallback);
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
        var overrideDirectory = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            yield return ExpandUserLogDirectory(overrideDirectory);

        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
            yield return Path.Combine(xdgStateHome, "cdidx", "logs");

        var xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCacheHome))
            yield return Path.Combine(xdgCacheHome, "cdidx", "logs");

        var xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
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

        yield return Path.Combine(Path.GetTempPath(), "cdidx", "logs");
    }

    private static bool CanWriteProbe(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, $".cdidx-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty, Encoding.UTF8);
            File.Delete(probePath);
            return true;
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

    private static void PruneOldLogs(string logDirectory, int retainedLogFileCount)
    {
        try
        {
            PrivateLogFile.PruneOldFiles(logDirectory, "stderr-*.log", retainedLogFileCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    private static void HardenLogFiles(string logDirectory)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            PrivateLogFile.HardenExisting(logDirectory, "stderr-*.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    private static void SetLogFilePermissions(string logPath)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            PrivateLogFile.TrySetPrivatePermissions(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
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
        var mode = Environment.GetEnvironmentVariable("CDIDX_LOG_REDACT");
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

        return arg.Contains("token", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("password", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("passwd", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("pwd", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("access-key", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("credential", StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactSensitiveText(string value)
    {
        var assignment = SensitiveAssignmentPattern.Match(value);
        if (assignment.Success)
            value = $"{assignment.Groups["name"].Value}={RedactedValue}";

        value = UriUserInfoPattern.Replace(value, match => $"{match.Groups["scheme"].Value}{match.Groups["user"].Value}:{RedactedValue}@");
        value = LongHexPattern.Replace(value, RedactedValue);
        value = LongBase64Pattern.Replace(value, RedactedValue);
        return value;
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
            var format = Environment.GetEnvironmentVariable(LogFormatEnvironmentVariable)?.Trim().ToLowerInvariant();
            if (format is not "json")
                format = "text";

            var retainCount = RetainedLogFileCount;
            if (int.TryParse(Environment.GetEnvironmentVariable(LogRetainEnvironmentVariable), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedRetain))
                retainCount = Math.Clamp(parsedRetain, 1, 10_000);

            var maxSizeBytes = DefaultLogMaxSizeBytes;
            if (int.TryParse(Environment.GetEnvironmentVariable(LogMaxSizeMbEnvironmentVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMb) && parsedMb > 0)
            {
                if (parsedMb <= MaxLogSizeMb)
                    maxSizeBytes = parsedMb * 1024L * 1024L;
            }
            else if (long.TryParse(Environment.GetEnvironmentVariable(GlobalToolLogMaxBytesEnvironmentVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBytes)
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
            TryWrite(() => secondary.Write(value));
        }

        public override void WriteLine(string? value)
        {
            TryWrite(() => primary.WriteLine(value));
            TryWrite(() => secondary.WriteLine(value));
        }

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
