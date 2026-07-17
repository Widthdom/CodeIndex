using CodeIndex.Database;
using System.Globalization;
using System.Reflection;

namespace CodeIndex.Cli;

internal enum RecoveryCommandShell
{
    PosixSh,
    PowerShell,
}

internal static class ExcerptRecoveryCommandFormatter
{
    private static readonly AsyncLocal<RecoveryInvocation?> ScopedInvocation = new();
    private static readonly RecoveryInvocation DefaultInvocation = new(["cdidx"], ResolveCurrentShell());

    public static void ApplyDbPath(FileExcerptResult excerpt, string dbPath)
    {
        ApplyDbPath(excerpt.ContentRecovery, excerpt.Path, dbPath);
    }

    public static void ApplyDbPath(ExcerptRecoveryHint? recovery, string path, string dbPath)
    {
        if (recovery is null)
            return;

        var invocation = ScopedInvocation.Value ?? DefaultInvocation;
        ApplyDbPath(recovery, path, dbPath, invocation.ArgvPrefix, invocation.Shell);
    }

    internal static void ApplyDbPath(
        ExcerptRecoveryHint recovery,
        string path,
        string dbPath,
        IReadOnlyList<string> invocationPrefix,
        RecoveryCommandShell shell)
    {
        var argv = BuildArgv(path, recovery.StartLine, recovery.EndLine, dbPath, invocationPrefix);
        recovery.Argv = argv;
        recovery.Command = RenderDisplayCommand(argv, shell);
        recovery.CommandShell = FormatShell(shell);
        recovery.CommandDisplayOnly = true;
    }

    internal static IDisposable UseCurrentProcessInvocation()
    {
        var prefix = ResolveInvocationPrefix(Environment.ProcessPath, Assembly.GetEntryAssembly()?.Location);
        var previous = ScopedInvocation.Value;
        ScopedInvocation.Value = new RecoveryInvocation(prefix, ResolveCurrentShell());
        return new InvocationScope(previous);
    }

    internal static string[] ResolveInvocationPrefix(string? processPath, string? entryAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return ["cdidx"];

        if (IsDotnetHost(processPath) && !string.IsNullOrWhiteSpace(entryAssemblyPath))
            return [processPath, Path.GetFullPath(entryAssemblyPath)];

        return [processPath];
    }

    internal static string RenderDisplayCommand(IReadOnlyList<string> argv, RecoveryCommandShell shell)
    {
        if (argv.Count == 0)
            return string.Empty;

        var rendered = string.Join(' ', argv.Select(argument => QuoteShellArgument(argument, shell)));
        return shell == RecoveryCommandShell.PowerShell && !IsSafeShellArgument(argv[0], shell)
            ? "& " + rendered
            : rendered;
    }

    private static List<string> BuildArgv(
        string path,
        int startLine,
        int endLine,
        string dbPath,
        IReadOnlyList<string> invocationPrefix)
    {
        var argv = new List<string>(invocationPrefix.Count + 12);
        argv.AddRange(invocationPrefix);
        argv.Add("excerpt");
        if (path.StartsWith("-", StringComparison.Ordinal))
            argv.Add("--");
        argv.Add(path);
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            argv.Add("--db");
            argv.Add(NormalizeDbPath(dbPath));
        }
        argv.Add("--start");
        argv.Add(startLine.ToString(CultureInfo.InvariantCulture));
        argv.Add("--end");
        argv.Add(endLine.ToString(CultureInfo.InvariantCulture));
        argv.Add("--max-line-width");
        argv.Add("0");
        argv.Add("--json");
        return argv;
    }

    private static string NormalizeDbPath(string dbPath)
    {
        if (dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return dbPath;

        var normalized = DbPathResolver.NormalizeDbPath(dbPath);
        return normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : Path.GetFullPath(normalized);
    }

    private static string QuoteShellArgument(string value, RecoveryCommandShell shell)
    {
        if (IsSafeShellArgument(value, shell))
            return value;

        return shell == RecoveryCommandShell.PowerShell
            ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool IsSafeShellArgument(string value, RecoveryCommandShell shell)
        => !string.IsNullOrEmpty(value) && value.All(c => IsSafeShellArgumentChar(c, shell));

    private static bool IsSafeShellArgumentChar(char c, RecoveryCommandShell shell)
        => char.IsLetterOrDigit(c)
            || c is '/' or '.' or '_' or '-' or ':'
            || (shell == RecoveryCommandShell.PowerShell && c == '\\');

    private static bool IsDotnetHost(string processPath)
        => string.Equals(
            Path.GetFileNameWithoutExtension(processPath.Replace('\\', '/')),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

    private static RecoveryCommandShell ResolveCurrentShell()
        => OperatingSystem.IsWindows() ? RecoveryCommandShell.PowerShell : RecoveryCommandShell.PosixSh;

    private static string FormatShell(RecoveryCommandShell shell)
        => shell == RecoveryCommandShell.PowerShell ? "powershell" : "posix-sh";

    private sealed record RecoveryInvocation(IReadOnlyList<string> ArgvPrefix, RecoveryCommandShell Shell);

    private sealed class InvocationScope(RecoveryInvocation? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            ScopedInvocation.Value = previous;
            _disposed = true;
        }
    }
}
