using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeIndex.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace CodeIndex.Indexer.Extensibility;

public static class ExtractorPluginRegistry
{
    public const int CurrentApiVersion = 1;
    internal const string TrustWorkspacePluginsEnvironmentVariable = "CDIDX_TRUST_WORKSPACE_PLUGINS";
    internal const int MaxPatternConfigBytes = 64 * 1024;
    internal const int MaxPatternRulesPerConfig = 128;
    internal const int MaxPatternRulesTotal = 128;
    internal const int MaxPatternRegexLength = 4096;
    internal const int MaxPluginAssemblyCandidatesPerDirectory = 128;
    internal const int MaxPluginAssemblyCandidatesTotal = 256;
    internal const long MaxPluginAssemblyBytes = 64 * 1024 * 1024;
    internal static readonly TimeSpan PatternRegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ISymbolExtractor> SymbolExtractors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReferenceExtractor> ReferenceExtractors = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoadedPluginAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoadedPatternConfigPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ExtractorRegistryDiagnostic> Diagnostics = [];
    private const int DiagnosticLimit = 20;
    private static int pluginAssemblyCount;
    private static int patternConfigCount;
    private static int skippedFileCount;
    private static int diagnosticTotalCount;
    private static int loadedPatternRuleCount;
    private static bool pluginsLoaded;

    public static IReadOnlyCollection<string> SymbolLanguages
    {
        get
        {
            EnsurePluginsLoaded();
            lock (Gate)
                return SymbolExtractors.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public static IReadOnlyCollection<string> ReferenceLanguages
    {
        get
        {
            EnsurePluginsLoaded();
            lock (Gate)
                return ReferenceExtractors.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public static IReadOnlyDictionary<string, string> LanguageExtensions
    {
        get
        {
            EnsurePluginsLoaded();
            lock (Gate)
            {
                var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AddLanguageExtensions(extensions, SymbolExtractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
                AddLanguageExtensions(extensions, ReferenceExtractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
                return extensions;
            }
        }
    }

    public static bool TryGetSymbolExtractor(string language, out ISymbolExtractor extractor)
    {
        EnsurePluginsLoaded();
        lock (Gate)
            return SymbolExtractors.TryGetValue(language, out extractor!);
    }

    public static bool TryGetReferenceExtractor(string language, out IReferenceExtractor extractor)
    {
        EnsurePluginsLoaded();
        lock (Gate)
            return ReferenceExtractors.TryGetValue(language, out extractor!);
    }

    internal static ExtractorRegistryStatus GetStatusSnapshot()
    {
        EnsurePluginsLoaded();
        lock (Gate)
        {
            return new ExtractorRegistryStatus
            {
                PluginAssemblyCount = pluginAssemblyCount,
                PatternConfigCount = patternConfigCount,
                SymbolExtractorCount = SymbolExtractors.Count,
                ReferenceExtractorCount = ReferenceExtractors.Count,
                SkippedFileCount = skippedFileCount,
                DiagnosticCount = diagnosticTotalCount,
                DiagnosticLimit = DiagnosticLimit,
                DiagnosticsTruncated = diagnosticTotalCount > Diagnostics.Count,
                Diagnostics = Diagnostics.Count == 0 ? null : Diagnostics.ToList(),
            };
        }
    }

    public static void Register(ISymbolExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        var language = NormalizePluginLanguage(extractor.Language);
        lock (Gate)
            SymbolExtractors[language] = extractor;
    }

    public static void Register(IReferenceExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        var language = NormalizePluginLanguage(extractor.Language);
        lock (Gate)
            ReferenceExtractors[language] = extractor;
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            SymbolExtractors.Clear();
            ReferenceExtractors.Clear();
            LoadedPluginAssemblyPaths.Clear();
            LoadedPatternConfigPaths.Clear();
            Diagnostics.Clear();
            pluginAssemblyCount = 0;
            patternConfigCount = 0;
            skippedFileCount = 0;
            diagnosticTotalCount = 0;
            loadedPatternRuleCount = 0;
            pluginsLoaded = true;
        }
    }

    internal static void ReloadForTests()
    {
        lock (Gate)
        {
            SymbolExtractors.Clear();
            ReferenceExtractors.Clear();
            LoadedPluginAssemblyPaths.Clear();
            LoadedPatternConfigPaths.Clear();
            Diagnostics.Clear();
            pluginAssemblyCount = 0;
            patternConfigCount = 0;
            skippedFileCount = 0;
            diagnosticTotalCount = 0;
            loadedPatternRuleCount = 0;
            pluginsLoaded = false;
        }
    }

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests()
        => EnumeratePluginAssemblyPaths().ToArray();

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests(string? projectRoot)
        => EnumeratePluginAssemblyPaths(EnumeratePluginDirectories(projectRoot)).ToArray();

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests(IReadOnlyList<string> directories)
        => EnumeratePluginAssemblyPaths(directories).ToArray();

    internal static void LoadPluginAssembliesForTests(IReadOnlyList<string> directories)
        => LoadPluginAssemblies(directories);

    internal static void LoadPluginForTests(string pluginPath)
        => TryLoadPlugin(pluginPath);

    internal static void LoadPluginsForProjectRoot(string? projectRoot)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(projectRoot) || !WorkspacePluginsTrusted())
            return;

        LoadPluginAssemblies(EnumerateWorkspacePluginDirectories(Path.GetFullPath(projectRoot)));
    }

    internal static void LoadPatternConfigsForProjectRoot(string? projectRoot)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        LoadPluginsForProjectRoot(projectRoot);
        foreach (var patternPath in EnumeratePatternConfigPaths(Path.GetFullPath(projectRoot)))
            TryLoadPatternConfig(patternPath);
    }

    internal static void LoadPatternConfigsForPath(string? path)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var directory = Path.GetFullPath(path);
        if (!Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory) ?? string.Empty;

        while (!string.IsNullOrEmpty(directory))
        {
            foreach (var patternPath in EnumeratePatternConfigPaths(directory, includeUserDirectory: false))
                TryLoadPatternConfig(patternPath);
            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        foreach (var patternPath in EnumerateUserPatternConfigPaths())
            TryLoadPatternConfig(patternPath);
    }

    private static void EnsurePluginsLoaded()
    {
        if (Volatile.Read(ref pluginsLoaded))
            return;

        lock (Gate)
        {
            if (pluginsLoaded)
                return;

            LoadPluginAssemblies(EnumeratePluginDirectories(projectRoot: null));
            pluginsLoaded = true;
        }
    }

    private static void LoadPluginAssemblies(IEnumerable<string> directories)
    {
        var pluginPaths = EnumeratePluginAssemblyPaths(directories).ToArray();
        foreach (var pluginPath in pluginPaths)
            TryLoadPlugin(pluginPath);
    }

    private static IEnumerable<string> EnumeratePluginAssemblyPaths()
        => EnumeratePluginAssemblyPaths(EnumeratePluginDirectories(projectRoot: null));

    private static IEnumerable<string> EnumeratePluginAssemblyPaths(IEnumerable<string> directories)
    {
        var totalCandidates = 0;
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            using var enumerator = TryEnumeratePluginFiles(directory);
            if (enumerator == null)
                continue;

            var directoryCandidates = 0;
            while (TryMoveNextPluginFile(directory, enumerator, out var pluginPath))
            {
                if (directoryCandidates >= MaxPluginAssemblyCandidatesPerDirectory)
                {
                    ReportPluginDirectorySkipped(
                        directory,
                        $"too many plugin assembly candidates (maximum {MaxPluginAssemblyCandidatesPerDirectory} per directory)");
                    break;
                }

                if (totalCandidates >= MaxPluginAssemblyCandidatesTotal)
                {
                    ReportPluginDirectorySkipped(
                        directory,
                        $"too many plugin assembly candidates (maximum {MaxPluginAssemblyCandidatesTotal} total)");
                    yield break;
                }

                directoryCandidates++;
                totalCandidates++;
                yield return pluginPath;
            }
        }
    }

    private static IEnumerator<string>? TryEnumeratePluginFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportPluginDirectorySkipped(directory, "could not enumerate plugin directory");
            return null;
        }
    }

    private static bool TryMoveNextPluginFile(string directory, IEnumerator<string> enumerator, out string pluginPath)
    {
        pluginPath = string.Empty;
        try
        {
            if (!enumerator.MoveNext())
                return false;

            pluginPath = enumerator.Current;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportPluginDirectorySkipped(directory, "could not enumerate plugin directory");
            return false;
        }
    }

    private static IEnumerable<string> EnumeratePluginDirectories(string? projectRoot)
    {
        if (WorkspacePluginsTrusted() && !string.IsNullOrWhiteSpace(projectRoot))
        {
            foreach (var directory in EnumerateWorkspacePluginDirectories(Path.GetFullPath(projectRoot)))
                yield return directory;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".cdidx", "plugins");
    }

    private static IEnumerable<string> EnumerateWorkspacePluginDirectories(string projectRoot)
    {
        yield return Path.Combine(projectRoot, ".cdidx", "plugins");
    }

    private static IEnumerable<string> EnumeratePatternConfigPaths(string workspaceRoot, bool includeUserDirectory = true)
    {
        foreach (var path in EnumeratePatternConfigPathsFromDirectory(
                     Path.Combine(workspaceRoot, ".cdidx", "patterns"),
                     workspaceRoot))
        {
            yield return path;
        }

        if (!includeUserDirectory)
            yield break;

        foreach (var path in EnumerateUserPatternConfigPaths())
            yield return path;
    }

    private static IEnumerable<string> EnumerateUserPatternConfigPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (var path in EnumeratePatternConfigPathsFromDirectory(
                         Path.Combine(home, ".config", "cdidx", "patterns"),
                         workspaceRoot: null))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumeratePatternConfigPathsFromDirectory(string directory, string? workspaceRoot)
    {
        if (!Directory.Exists(directory) || !PatternDirectoryIsSafe(directory, workspaceRoot))
            yield break;

        foreach (var path in EnumeratePatternFiles(directory, "*.yaml"))
            yield return path;
        foreach (var path in EnumeratePatternFiles(directory, "*.yml"))
            yield return path;
    }

    private static IEnumerable<string> EnumeratePatternFiles(string directory, string searchPattern)
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportPatternDirectoryRejected(directory, "could not enumerate pattern directory");
            yield break;
        }

        foreach (var path in paths)
            yield return path;
    }

    private static bool PatternDirectoryIsSafe(string directory, string? workspaceRoot)
    {
        if (workspaceRoot != null)
        {
            var workspaceCdidxDirectory = Path.Combine(workspaceRoot, ".cdidx");
            if (DirectoryIsSymlinkOrReparsePoint(workspaceCdidxDirectory))
            {
                ReportPatternDirectoryRejected(workspaceCdidxDirectory, "symbolic links and reparse points are not supported");
                return false;
            }
        }

        if (DirectoryIsSymlinkOrReparsePoint(directory))
        {
            ReportPatternDirectoryRejected(directory, "symbolic links and reparse points are not supported");
            return false;
        }

        return true;
    }

    private static bool DirectoryIsSymlinkOrReparsePoint(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            return (info.Attributes & FileAttributes.ReparsePoint) != 0
                   || !string.IsNullOrEmpty(info.LinkTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportPatternDirectoryRejected(directory, "could not inspect pattern directory");
            return true;
        }
    }

    private static void TryLoadPatternConfig(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            lock (Gate)
            {
                if (!LoadedPatternConfigPaths.Add(path))
                    return;
            }

            var configLines = TryReadPatternConfigLines(path);
            if (configLines == null)
                return;

            var language = string.Empty;
            var extensions = new List<string>();
            var patterns = new List<ConfiguredSymbolExtractor.PatternRule>();
            string? pendingKind = null;
            foreach (var rawLine in configLines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (TryReadScalar(line, "language", out var value))
                {
                    language = NormalizePluginLanguage(value);
                }
                else if (TryReadScalar(line.TrimStart('-').Trim(), "extension", out value))
                {
                    extensions.Add(NormalizePluginExtension(value) ?? value);
                }
                else if (TryReadScalar(line.TrimStart('-').Trim(), "kind", out value))
                {
                    pendingKind = value.Trim();
                }
                else if (TryReadScalar(line.TrimStart('-').Trim(), "regex", out value) && pendingKind != null)
                {
                    if (patterns.Count >= MaxPatternRulesPerConfig)
                    {
                        ReportPatternConfigRejected(path, $"too many pattern rules (maximum {MaxPatternRulesTotal})");
                        return;
                    }

                    if (value.Length > MaxPatternRegexLength)
                    {
                        ReportPatternConfigRejected(path, $"regex for kind '{pendingKind}' is too long ({value.Length} characters; maximum {MaxPatternRegexLength})");
                        return;
                    }

                    if (!TryReservePatternRuleBudget(path))
                        return;

                    Regex regex;
                    try
                    {
                        regex = new Regex(
                            value,
                            RegexOptions.Compiled | RegexOptions.CultureInvariant,
                            PatternRegexTimeout);
                    }
                    catch (ArgumentException)
                    {
                        ReportPatternConfigRejected(path, $"invalid regex for kind '{DiagnosticSanitizer.ForMessage(pendingKind)}'");
                        return;
                    }

                    patterns.Add(new ConfiguredSymbolExtractor.PatternRule(
                        pendingKind,
                        regex));
                    pendingKind = null;
                }
            }

            if (language.Length > 0 && patterns.Count > 0)
            {
                Register(new ConfiguredSymbolExtractor(language, extensions, patterns));
                lock (Gate)
                    patternConfigCount++;
            }
            else
            {
                ReportPatternConfigSkipped(path, "missing language or regex patterns");
            }
        }
        catch (Exception)
        {
            ReportPatternConfigRejected(path, "could not parse pattern config");
        }
    }

    private static void ReportPatternConfigRejected(string path, string reason)
    {
        Console.Error.WriteLine($"[cdidx] Skipped pattern config '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordDiagnostic(
            "pattern",
            path,
            typeName: null,
            severity: "error",
            $"Pattern config skipped: {reason}",
            countsAsSkippedFile: true);
    }

    private static void ReportPatternConfigSkipped(string path, string reason)
    {
        Console.Error.WriteLine($"[cdidx] Skipped pattern config '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordDiagnostic(
            "pattern",
            path,
            typeName: null,
            severity: "skipped",
            $"Pattern config skipped: {reason}",
            countsAsSkippedFile: true);
    }

    private static void ReportPatternDirectoryRejected(string path, string reason)
    {
        Console.Error.WriteLine($"[cdidx] Skipped pattern directory '{DiagnosticSanitizer.ForPath(path)}': {DiagnosticSanitizer.ForMessage(reason)}.");
        RecordDiagnostic(
            "pattern_directory",
            path,
            typeName: null,
            severity: "error",
            $"Pattern directory skipped: {reason}",
            countsAsSkippedFile: false);
    }

    private static void ReportPluginDirectorySkipped(string path, string reason)
    {
        RecordDiagnostic(
            "plugin_directory",
            path,
            typeName: null,
            severity: "skipped",
            $"Plugin directory skipped: {reason}.",
            countsAsSkippedFile: false);
    }

    private static bool TryReservePatternRuleBudget(string path)
    {
        lock (Gate)
        {
            if (loadedPatternRuleCount >= MaxPatternRulesTotal)
            {
                ReportPatternConfigRejected(path, $"too many pattern rules (maximum {MaxPatternRulesTotal})");
                return false;
            }

            loadedPatternRuleCount++;
            return true;
        }
    }

    private static IReadOnlyList<string>? TryReadPatternConfigLines(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            ReportPatternConfigRejected(path, "file does not exist");
            return null;
        }

        var attributes = fileInfo.Attributes;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            ReportPatternConfigRejected(path, "path is a directory");
            return null;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0 || !string.IsNullOrEmpty(fileInfo.LinkTarget))
        {
            ReportPatternConfigRejected(path, "symbolic links and reparse points are not supported");
            return null;
        }

        if (fileInfo.Length > MaxPatternConfigBytes)
        {
            ReportPatternConfigRejected(path, $"file is too large ({fileInfo.Length} bytes; maximum {MaxPatternConfigBytes})");
            return null;
        }

        var bytes = OperatingSystem.IsWindows()
            ? TryReadWindowsPatternConfigBytes(path)
            : TryReadUnixPatternConfigBytes(path);
        if (bytes == null)
            return null;

        var text = Encoding.UTF8.GetString(bytes);
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static byte[]? TryReadWindowsPatternConfigBytes(string path)
    {
        using var handle = CreateFile(
            path,
            GenericRead,
            FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: IntPtr.Zero,
            creationDisposition: FileMode.Open,
            flagsAndAttributes: FileAttributes.Normal | FileFlagOpenReparsePoint,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            ReportPatternConfigRejected(path, $"could not open safely (errno {Marshal.GetLastPInvokeError()})");
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            ReportPatternConfigRejected(path, $"could not inspect file handle (errno {Marshal.GetLastPInvokeError()})");
            return null;
        }

        var attributes = (FileAttributes)info.FileAttributes;
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            ReportPatternConfigRejected(path, "path is not a regular file");
            return null;
        }

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (size > MaxPatternConfigBytes)
        {
            ReportPatternConfigRejected(path, $"file is too large ({size} bytes; maximum {MaxPatternConfigBytes})");
            return null;
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 8192, isAsync: false);
        return TryReadBoundedPatternConfigBytes(path, stream);
    }

    private static byte[]? TryReadUnixPatternConfigBytes(string path)
    {
        var fd = UnixOpen(path, GetUnixOpenFlags());
        if (fd < 0)
        {
            ReportPatternConfigRejected(path, $"could not open safely (errno {Marshal.GetLastPInvokeError()})");
            return null;
        }

        try
        {
            if (!TryGetUnixFileType(fd, out var mode) || !IsRegularUnixFile(mode))
            {
                ReportPatternConfigRejected(path, "path is not a regular file");
                return null;
            }

            using var stream = new MemoryStream(MaxPatternConfigBytes + 1);
            var buffer = new byte[Math.Min(8192, MaxPatternConfigBytes + 1)];
            while (stream.Length <= MaxPatternConfigBytes)
            {
                var remaining = MaxPatternConfigBytes + 1 - (int)stream.Length;
                if (remaining <= 0)
                    break;

                var bytesRead = UnixRead(fd, buffer, (UIntPtr)Math.Min(buffer.Length, remaining));
                if (bytesRead == 0)
                    break;
                if (bytesRead < 0)
                {
                    ReportPatternConfigRejected(path, $"could not read safely (errno {Marshal.GetLastPInvokeError()})");
                    return null;
                }

                stream.Write(buffer, 0, (int)bytesRead);
            }

            return ValidatePatternConfigBytes(path, stream.ToArray());
        }
        finally
        {
            _ = UnixClose(fd);
        }
    }

    private static byte[]? TryReadBoundedPatternConfigBytes(string path, Stream stream)
    {
        using var output = new MemoryStream(MaxPatternConfigBytes + 1);
        var buffer = new byte[Math.Min(8192, MaxPatternConfigBytes + 1)];
        while (output.Length <= MaxPatternConfigBytes)
        {
            var remaining = MaxPatternConfigBytes + 1 - (int)output.Length;
            if (remaining <= 0)
                break;

            var bytesRead = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (bytesRead == 0)
                break;

            output.Write(buffer, 0, bytesRead);
        }

        return ValidatePatternConfigBytes(path, output.ToArray());
    }

    private static byte[]? ValidatePatternConfigBytes(string path, byte[] bytes)
    {
        if (bytes.Length <= MaxPatternConfigBytes)
            return bytes;

        ReportPatternConfigRejected(path, $"file is too large (more than {MaxPatternConfigBytes} bytes)");
        return null;
    }

    private static bool TryGetUnixFileType(int fd, out uint mode)
    {
        mode = 0;
        var modeOffset = GetUnixStatModeOffset();
        if (modeOffset < 0)
            return false;

        var stat = new byte[UnixStatBufferBytes];
        try
        {
            if (UnixFStat(fd, stat) != 0)
                return false;

            mode = BitConverter.ToUInt32(stat, modeOffset);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static int LinuxStatModeOffsetForTests(Architecture architecture)
        => LinuxStatModeOffset(architecture);

    private static int GetUnixStatModeOffset()
    {
        if (OperatingSystem.IsMacOS())
            return 4;

        return OperatingSystem.IsLinux()
            ? LinuxStatModeOffset(RuntimeInformation.ProcessArchitecture)
            : -1;
    }

    private static int LinuxStatModeOffset(Architecture architecture)
        => architecture switch
        {
            Architecture.X64 => 24,
            Architecture.Arm64 => 16,
            _ => -1,
        };

    private static bool IsRegularUnixFile(uint mode)
    {
        const uint fileTypeMask = 0xF000;
        const uint regularFile = 0x8000;
        return (mode & fileTypeMask) == regularFile;
    }

    private const uint GenericRead = 0x80000000;
    private const FileAttributes FileFlagOpenReparsePoint = (FileAttributes)0x00200000;
    private const int UnixStatBufferBytes = 256;

    private static int GetUnixOpenFlags()
    {
        const int oReadOnly = 0;
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return oReadOnly | 0x0004 | 0x00000100 | 0x01000000;

        return oReadOnly | 0x800 | 0x20000 | 0x80000;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint UnixRead(int fd, byte[] buffer, UIntPtr count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int fd);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int UnixFStat(int fd, [Out] byte[] stat);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
        [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle fileHandle, out WindowsFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static bool TryReadScalar(string line, string key, out string value)
    {
        value = string.Empty;
        var prefix = key + ":";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        value = line[prefix.Length..].Trim().Trim('"', '\'').Replace("\\\\", "\\", StringComparison.Ordinal);
        return value.Length > 0;
    }

    private static void TryLoadPlugin(string pluginPath)
    {
        var fullPath = pluginPath;
        try
        {
            fullPath = Path.GetFullPath(pluginPath);
            lock (Gate)
            {
                if (!LoadedPluginAssemblyPaths.Add(fullPath))
                    return;
            }

            if (!PluginAssemblyCandidateIsWithinBudget(fullPath))
                return;

            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
            var attribute = assembly.GetCustomAttribute<CdidxPluginAttribute>();
            if (attribute == null)
            {
                RecordDiagnostic(
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    "Plugin assembly skipped: missing CdidxPluginAttribute.",
                    countsAsSkippedFile: true);
                return;
            }

            if (attribute.MinApiVersion > CurrentApiVersion
                || attribute.MaxApiVersion < CurrentApiVersion)
            {
                RecordDiagnostic(
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    $"Plugin assembly skipped: API range {attribute.MinApiVersion}-{attribute.MaxApiVersion} does not include {CurrentApiVersion}.",
                    countsAsSkippedFile: true);
                return;
            }

            lock (Gate)
                pluginAssemblyCount++;

            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsAbstract: false, IsInterface: false } && type.GetConstructor(Type.EmptyTypes) != null)
                    TryRegisterPluginType(type, fullPath);
            }
        }
        catch (Exception)
        {
            RecordDiagnostic(
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Failed to load plugin assembly.",
                countsAsSkippedFile: true);
        }
    }

    private static bool PluginAssemblyCandidateIsWithinBudget(string fullPath)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RecordDiagnostic(
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: could not inspect file.",
                countsAsSkippedFile: true);
            return false;
        }

        if (!fileInfo.Exists)
        {
            RecordDiagnostic(
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: file does not exist.",
                countsAsSkippedFile: true);
            return false;
        }

        if ((fileInfo.Attributes & FileAttributes.Directory) != 0)
        {
            RecordDiagnostic(
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: path is a directory.",
                countsAsSkippedFile: true);
            return false;
        }

        if (fileInfo.Length > MaxPluginAssemblyBytes)
        {
            RecordDiagnostic(
                "plugin",
                fullPath,
                typeName: null,
                severity: "skipped",
                $"Plugin assembly skipped: file is too large ({fileInfo.Length} bytes; maximum {MaxPluginAssemblyBytes}).",
                countsAsSkippedFile: true);
            return false;
        }

        return true;
    }

    private static void TryRegisterPluginType(Type type, string pluginPath)
    {
        try
        {
            if (typeof(ISymbolExtractor).IsAssignableFrom(type)
                && Activator.CreateInstance(type) is ISymbolExtractor symbolExtractor)
            {
                Register(symbolExtractor);
            }

            if (typeof(IReferenceExtractor).IsAssignableFrom(type)
                && Activator.CreateInstance(type) is IReferenceExtractor referenceExtractor)
            {
                Register(referenceExtractor);
            }
        }
        catch (Exception)
        {
            RecordDiagnostic(
                "plugin_type",
                pluginPath,
                type.FullName,
                severity: "error",
                "Failed to instantiate plugin type.",
                countsAsSkippedFile: false);
        }
    }

    private static void RecordDiagnostic(
        string kind,
        string path,
        string? typeName,
        string severity,
        string message,
        bool countsAsSkippedFile)
    {
        lock (Gate)
        {
            diagnosticTotalCount++;
            if (countsAsSkippedFile)
                skippedFileCount++;
            if (Diagnostics.Count < DiagnosticLimit)
                Diagnostics.Add(new ExtractorRegistryDiagnostic(
                    DiagnosticSanitizer.ForMessage(kind),
                    DiagnosticSanitizer.ForPath(path),
                    DiagnosticSanitizer.ForOptionalLabel(typeName),
                    DiagnosticSanitizer.ForMessage(severity),
                    DiagnosticSanitizer.ForMessage(message)));
        }
    }

    private static bool WorkspacePluginsTrusted()
    {
        var value = Environment.GetEnvironmentVariable(TrustWorkspacePluginsEnvironmentVariable);
        return value != null
               && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddLanguageExtensions(
        Dictionary<string, string> target,
        IEnumerable<(string Language, IReadOnlyCollection<string> FileExtensions)> plugins)
    {
        foreach (var (language, fileExtensions) in plugins)
        {
            var normalizedLanguage = NormalizePluginLanguage(language);
            foreach (var extension in fileExtensions)
            {
                var normalizedExtension = NormalizePluginExtension(extension);
                if (normalizedExtension != null)
                    target.TryAdd(normalizedExtension, normalizedLanguage);
            }
        }
    }

    private static string NormalizePluginLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Plugin language must be non-empty.", nameof(language));

        return language.Trim().ToLowerInvariant();
    }

    private static string? NormalizePluginExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        extension = extension.Trim().ToLowerInvariant();
        return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
    }
}

public sealed class ExtractorRegistryStatus
{
    [JsonPropertyName("plugin_assembly_count")]
    public int PluginAssemblyCount { get; init; }
    [JsonPropertyName("pattern_config_count")]
    public int PatternConfigCount { get; init; }
    [JsonPropertyName("symbol_extractor_count")]
    public int SymbolExtractorCount { get; init; }
    [JsonPropertyName("reference_extractor_count")]
    public int ReferenceExtractorCount { get; init; }
    [JsonPropertyName("skipped_file_count")]
    public int SkippedFileCount { get; init; }
    [JsonPropertyName("diagnostic_count")]
    public int DiagnosticCount { get; init; }
    [JsonPropertyName("diagnostic_limit")]
    public int DiagnosticLimit { get; init; }
    [JsonPropertyName("diagnostics_truncated")]
    public bool DiagnosticsTruncated { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtractorRegistryDiagnostic>? Diagnostics { get; init; }
}

public sealed record ExtractorRegistryDiagnostic(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type_name")] string? TypeName,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message);
