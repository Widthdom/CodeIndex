using CodeIndex.Database;
using CodeIndex.Diagnostics;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

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

    public static void ApplyDbPath(FileExcerptResult excerpt, string dbPath, bool redactPaths = true)
    {
        ApplyDbPath(excerpt.ContentRecovery, excerpt.Path, dbPath, redactPaths);
    }

    public static void ApplyDbPath(ExcerptRecoveryHint? recovery, string path, string dbPath, bool redactPaths = true)
    {
        if (recovery is null)
            return;

        var invocation = ScopedInvocation.Value ?? DefaultInvocation;
        ApplyDbPath(recovery, path, dbPath, invocation.ArgvPrefix, invocation.Shell, redactPaths);
    }

    internal static void ApplyDbPath(
        ExcerptRecoveryHint recovery,
        string path,
        string dbPath,
        IReadOnlyList<string> invocationPrefix,
        RecoveryCommandShell shell,
        bool redactPaths = true)
    {
        var resolvedArgv = BuildArgv(
            path,
            recovery.StartLine,
            recovery.EndLine,
            recovery.StartColumn,
            recovery.EndColumn,
            dbPath,
            invocationPrefix);
        var outputArgv = redactPaths
            ? BuildSupportSafeArgv(resolvedArgv, invocationPrefix.Count, path.StartsWith("-", StringComparison.Ordinal))
            : resolvedArgv;
        recovery.Argv = outputArgv;
        recovery.Command = RenderDisplayCommand(outputArgv, shell);
        recovery.CommandShell = FormatShell(shell);
        recovery.CommandDisplayOnly = redactPaths;
        recovery.PathsRedacted = redactPaths;
        recovery.RequiresLocalPathSubstitution = redactPaths && !resolvedArgv.SequenceEqual(outputArgv, StringComparer.Ordinal);
    }

    internal static IDisposable UseCurrentProcessInvocation()
    {
        var prefix = ResolveInvocationPrefix(Environment.ProcessPath, Environment.GetCommandLineArgs().FirstOrDefault());
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

    internal static string RenderDisplayCommandForCurrentShell(IReadOnlyList<string> argv)
        => RenderDisplayCommand(argv, ResolveCurrentShell());

    private static List<string> BuildArgv(
        string path,
        int startLine,
        int endLine,
        int? startColumn,
        int? endColumn,
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
        if (startColumn.HasValue)
        {
            argv.Add("--start-column");
            argv.Add(startColumn.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (endColumn.HasValue)
        {
            argv.Add("--end-column");
            argv.Add(endColumn.Value.ToString(CultureInfo.InvariantCulture));
        }
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

    private static List<string> BuildSupportSafeArgv(
        IReadOnlyList<string> resolvedArgv,
        int invocationPrefixCount,
        bool hasEndOfOptionsMarker)
    {
        var output = resolvedArgv.ToList();
        for (var index = 0; index < invocationPrefixCount; index++)
            output[index] = RedactAbsolutePathArgument(output[index]);

        var sourcePathIndex = invocationPrefixCount + (hasEndOfOptionsMarker ? 2 : 1);
        if (sourcePathIndex < output.Count)
            output[sourcePathIndex] = RedactAbsolutePathArgument(output[sourcePathIndex]);

        var dbFlagIndex = output.FindIndex(sourcePathIndex + 1, argument => argument == "--db");
        if (dbFlagIndex >= 0 && dbFlagIndex + 1 < output.Count)
            output[dbFlagIndex + 1] = RedactAbsolutePathArgument(output[dbFlagIndex + 1]);

        return output;
    }

    private static string RedactAbsolutePathArgument(string value)
    {
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return DiagnosticSanitizer.ForSupportSafePath(value);

        return IsAbsolutePathArgument(value)
            ? DiagnosticSanitizer.ForSupportSafePath(value)
            : value;
    }

    private static bool IsAbsolutePathArgument(string value)
        => Path.IsPathRooted(value)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || (value.Length >= 3
                && char.IsAsciiLetter(value[0])
                && value[1] == ':'
                && value[2] is '/' or '\\');

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
        => ResolveShell(
            TryGetParentProcessName(),
            OperatingSystem.IsWindows(),
            hasMsysEnvironment: !string.IsNullOrWhiteSpace(CdidxEnvironment.GetEnvironmentVariable("MSYSTEM")));

    internal static RecoveryCommandShell ResolveShell(
        string? parentProcessName,
        bool isWindows,
        bool hasMsysEnvironment)
    {
        var normalizedParent = Path.GetFileNameWithoutExtension(parentProcessName?.Replace('\\', '/') ?? string.Empty)
            .ToLowerInvariant();
        if (normalizedParent is "pwsh" or "powershell" or "powershell_ise")
            return RecoveryCommandShell.PowerShell;
        if (normalizedParent is "sh" or "bash" or "zsh" or "dash" or "ksh" or "fish" or "git-bash")
            return RecoveryCommandShell.PosixSh;

        if (hasMsysEnvironment)
            return RecoveryCommandShell.PosixSh;
        return isWindows ? RecoveryCommandShell.PowerShell : RecoveryCommandShell.PosixSh;
    }

    private static string? TryGetParentProcessName()
    {
        try
        {
            var parentProcessId = OperatingSystem.IsWindows()
                ? TryGetWindowsParentProcessId(Environment.ProcessId)
                : GetParentProcessId();
            if (!parentProcessId.HasValue || parentProcessId.Value <= 0)
                return null;

            using var parent = Process.GetProcessById(parentProcessId.Value);
            return parent.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static int? TryGetWindowsParentProcessId(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandleValue)
            return null;

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            };
            if (!Process32First(snapshot, ref entry))
                return null;

            do
            {
                if (entry.ProcessId == processId)
                    return checked((int)entry.ParentProcessId);
            }
            while (Process32Next(snapshot, ref entry));
            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("libc")]
    private static extern int getppid();

    private static int GetParentProcessId() => getppid();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal IntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExecutableFile;
    }

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
