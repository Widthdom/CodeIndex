using System.Text;
using System.Threading;

namespace CodeIndex.Cli;

/// <summary>
/// Best-effort stderr/lifecycle log sink for non-development executions.
/// 開発実行以外向けのベストエフォートな stderr/ライフサイクルログ出力先。
/// </summary>
internal static class GlobalToolLog
{
    private const int RetainedLogFileCount = 30;
    private static readonly AsyncLocal<Session?> CurrentSession = new();

    internal static IDisposable? TryStart(string[] args, string appVersion)
    {
        try
        {
            if (!ShouldEnable())
                return null;

            var logDirectory = ResolveLogDirectory();
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, $"stderr-{DateTime.UtcNow:yyyyMMdd}.log");
            var writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            PruneOldLogs(logDirectory);

            var session = new Session(writer, logPath);
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
            CurrentSession.Value = null;
            return null;
        }
    }

    internal static void Info(string message) => CurrentSession.Value?.Write("INFO", message);

    internal static void Error(string message) => CurrentSession.Value?.Write("ERROR", message);

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

    private static bool LooksLikeDevelopmentExecution(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/src/CodeIndex/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/tests/CodeIndex.Tests/bin/", StringComparison.OrdinalIgnoreCase);
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
        var overrideDirectory = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(ExpandUserLogDirectory(overrideDirectory));

        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
            return Path.Combine(xdgStateHome, "cdidx", "logs");

        var xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCacheHome))
            return Path.Combine(xdgCacheHome, "cdidx", "logs");

        var xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDir))
            return Path.Combine(xdgRuntimeDir, "cdidx", "logs");

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                return Path.Combine(localAppData, "cdidx", "logs");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, "Library", "Logs", "cdidx");

        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".local", "state", "cdidx", "logs");

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(fallback))
            return Path.Combine(fallback, "cdidx", "logs");

        return Path.Combine(Path.GetTempPath(), "cdidx", "logs");
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

    private static void PruneOldLogs(string logDirectory)
    {
        try
        {
            var oldLogs = new DirectoryInfo(logDirectory)
                .EnumerateFiles("stderr-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(RetainedLogFileCount)
                .ToList();

            foreach (var file in oldLogs)
                file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only / ベストエフォートのみ
        }
    }

    private static string FormatArgs(string[] args)
    {
        if (args.Length == 0)
            return "<none>";

        return string.Join(" ", args.Select(QuoteArg));
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
        private TextWriter? _originalError;
        private TextWriter? _teeError;
        private bool _disposed;

        public Session(StreamWriter writer, string logPath)
        {
            _writer = writer;
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
                    _writer.WriteLine($"{DateTimeOffset.UtcNow:O} [{level}] {message}");
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

    private sealed class TeeTextWriter(TextWriter primary, TextWriter secondary) : TextWriter
    {
        public override Encoding Encoding => primary.Encoding;

        public override void Flush()
        {
            primary.Flush();
            secondary.Flush();
        }

        public override void Write(char value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void Write(string? value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void WriteLine(string? value)
        {
            primary.WriteLine(value);
            secondary.WriteLine(value);
        }
    }
}
