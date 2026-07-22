using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Models;
using Microsoft.Win32.SafeHandles;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    private static void TryLoadPatternConfig(
        PatternWorkspaceState state,
        string path,
        string source,
        Func<string, Stream>? openFile = null,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput = null)
    {
        var inputObserved = false;
        Action<string, ReadOnlyMemory<byte>?, long?>? trackedObserver = observeInput == null
            ? null
            : (observedPath, content, observedLength) =>
            {
                inputObserved = true;
                observeInput(observedPath, content, observedLength);
            };
        try
        {
            path = Path.GetFullPath(path);
            lock (state.Gate)
            {
                if (state.Retired || PatternConfigPathIsLoaded(state, path))
                    return;
            }

            var configText = TryReadPatternConfigText(state, path, openFile, trackedObserver);
            if (configText == null)
                return;

            var fingerprint = CreatePatternConfigFingerprint(path, configText, includeMetadata: openFile == null);
            lock (state.Gate)
            {
                if (state.Retired
                    || PatternConfigPathIsLoaded(state, path)
                    || (TryGetFailedPatternConfigFingerprint(state, path, out var failedFingerprint)
                        && failedFingerprint == fingerprint))
                    return;
            }

            var parseResult = ParsePatternConfig(path, configText);
            if (!parseResult.Success)
            {
                lock (state.Gate)
                {
                    if (state.Retired
                        || PatternConfigPathIsLoaded(state, path)
                        || (TryGetFailedPatternConfigFingerprint(state, path, out var failedFingerprint)
                            && failedFingerprint == fingerprint))
                    {
                        return;
                    }

                    SetFailedPatternConfigFingerprint(state, path, fingerprint);
                }

                if (parseResult.Incomplete)
                    ReportPatternConfigSkipped(state, path, parseResult.FailureReason!);
                else
                    ReportPatternConfigRejected(state, path, parseResult.FailureReason!);
                return;
            }

            lock (state.Gate)
            {
                if (state.Retired || PatternConfigPathIsLoaded(state, path))
                    return;

                var patterns = parseResult.Patterns!;
                if (state.RuleCount > MaxPatternRulesTotal - patterns.Count)
                {
                    SetFailedPatternConfigFingerprint(state, path, fingerprint);
                    ReportPatternConfigRejected(state, path, $"too many pattern rules (maximum {MaxPatternRulesTotal})");
                    return;
                }

                var configuredExtractor = new ConfiguredSymbolExtractor(
                    parseResult.Language!,
                    parseResult.Extensions!,
                    patterns,
                    (sourcePath, language, kind) => ReportPatternExtractorTimeout(state, sourcePath, language, kind));
                if (!state.PatternSources.TryGetValue(parseResult.Language!, out var existingSource)
                    || string.Equals(source, "user", StringComparison.Ordinal)
                    || !string.Equals(existingSource, "user", StringComparison.Ordinal))
                {
                    state.PatternSymbolExtractors[parseResult.Language!] = configuredExtractor;
                    state.PatternSources[parseResult.Language!] = source;
                }
                state.RuleCount += patterns.Count;
                state.ConfigCount++;
                TryMarkPatternConfigPathLoaded(state, path);
                RemoveFailedPatternConfigFingerprint(state, path);
                state.Configs.Add(new PatternConfigStatus(
                    DiagnosticSanitizer.ForPath(path),
                    source,
                    parseResult.Language!,
                    patterns.Count));
                state.PublishSnapshot();
            }
        }
        catch (Exception ex) when (ex is not IFileSystemAuthorizationFailure)
        {
            if (!inputObserved)
                observeInput?.Invoke(path, null, null);
            ReportPatternConfigRejected(state, path, "could not parse pattern config");
        }
    }

    private static PatternConfigParseResult ParsePatternConfig(string path, string configText)
    {
        var language = string.Empty;
        var extensions = new List<string>();
        var patterns = new List<ConfiguredSymbolExtractor.PatternRule>();
        string? pendingKind = null;
        var remaining = configText.AsSpan();
        while (TryReadNextPatternConfigLine(ref remaining, out var rawLine))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var itemLine = TrimPatternConfigListMarker(line);
            var scalarResult = TryReadScalar(line, "language", MaxPatternLanguageLength, out var value, out var scalarLength);
            if (scalarResult == PatternScalarReadResult.TooLong)
                return PatternConfigParseResult.Rejected($"language scalar is too long ({scalarLength} characters; maximum {MaxPatternLanguageLength})");

            if (scalarResult == PatternScalarReadResult.Success)
            {
                language = NormalizePluginLanguage(value);
                continue;
            }

            scalarResult = TryReadScalar(itemLine, "extension", MaxPatternExtensionLength, out value, out scalarLength);
            if (scalarResult == PatternScalarReadResult.TooLong)
                return PatternConfigParseResult.Rejected($"extension scalar is too long ({scalarLength} characters; maximum {MaxPatternExtensionLength})");

            if (scalarResult == PatternScalarReadResult.Success)
            {
                var extension = NormalizePluginExtension(value) ?? value;
                if (extension.Length > MaxPatternExtensionLength)
                    return PatternConfigParseResult.Rejected($"extension scalar is too long ({extension.Length} characters; maximum {MaxPatternExtensionLength})");

                extensions.Add(extension);
                continue;
            }

            scalarResult = TryReadScalar(itemLine, "kind", MaxPatternKindLength, out value, out scalarLength);
            if (scalarResult == PatternScalarReadResult.TooLong)
                return PatternConfigParseResult.Rejected($"kind scalar is too long ({scalarLength} characters; maximum {MaxPatternKindLength})");

            if (scalarResult == PatternScalarReadResult.Success)
            {
                pendingKind = value.Trim();
                continue;
            }

            if (!TryReadScalar(itemLine, "regex", out value) || pendingKind == null)
                continue;

            if (patterns.Count >= MaxPatternRulesPerConfig)
                return PatternConfigParseResult.Rejected($"too many pattern rules (maximum {MaxPatternRulesPerConfig})");

            if (!SymbolKindCatalog.IsValidSymbolKind(pendingKind))
                return PatternConfigParseResult.Rejected($"unknown symbol kind '{DiagnosticSanitizer.ForMessage(pendingKind)}'");

            if (value.Length > MaxPatternRegexLength)
                return PatternConfigParseResult.Rejected($"regex for kind '{DiagnosticSanitizer.ForMessage(pendingKind)}' is too long ({value.Length} characters; maximum {MaxPatternRegexLength})");

            Regex regex;
            try
            {
                regex = Regex.CreateExtractionRegex(
                    value,
                    RegexOptions.Compiled,
                    PatternRegexTimeout);
            }
            catch (ArgumentException)
            {
                return PatternConfigParseResult.Rejected($"invalid regex for kind '{DiagnosticSanitizer.ForMessage(pendingKind)}'");
            }

            patterns.Add(new ConfiguredSymbolExtractor.PatternRule(
                pendingKind,
                regex,
                path));
            pendingKind = null;
        }

        return language.Length > 0 && patterns.Count > 0
            ? PatternConfigParseResult.Accepted(language, extensions, patterns)
            : PatternConfigParseResult.Skipped("missing language or regex patterns");
    }

    private static PatternConfigFingerprint CreatePatternConfigFingerprint(
        string path,
        string configText,
        bool includeMetadata)
    {
        long length = 0;
        long lastWriteUtcTicks = 0;
        try
        {
            if (includeMetadata)
            {
                var fileInfo = new FileInfo(path);
                length = fileInfo.Length;
                lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The content hash still makes repaired content retryable when metadata is unavailable.
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configText)));
        return new PatternConfigFingerprint(length, lastWriteUtcTicks, contentHash);
    }

    private sealed record PatternConfigParseResult(
        bool Success,
        bool Incomplete,
        string? Language,
        IReadOnlyList<string>? Extensions,
        IReadOnlyList<ConfiguredSymbolExtractor.PatternRule>? Patterns,
        string? FailureReason)
    {
        internal static PatternConfigParseResult Accepted(
            string language,
            IReadOnlyList<string> extensions,
            IReadOnlyList<ConfiguredSymbolExtractor.PatternRule> patterns)
            => new(true, false, language, extensions, patterns, null);

        internal static PatternConfigParseResult Rejected(string reason)
            => new(false, false, null, null, null, reason);

        internal static PatternConfigParseResult Skipped(string reason)
            => new(false, true, null, null, null, reason);
    }

    private sealed record PatternConfigFingerprint(long Length, long LastWriteUtcTicks, string ContentHash);

    private static string? TryReadPatternConfigText(
        PatternWorkspaceState state,
        string path,
        Func<string, Stream>? openFile,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput)
    {
        if (openFile != null)
        {
            using var stream = openFile(path);
            return TryReadBoundedPatternConfigText(state, path, stream, observeInput);
        }

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            observeInput?.Invoke(path, null, null);
            ReportPatternConfigRejected(state, path, "file does not exist");
            return null;
        }

        var attributes = fileInfo.Attributes;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            ReportPatternConfigRejected(state, path, "path is a directory");
            return null;
        }

        if (FileSystemBoundary.IsSymlinkOrReparsePoint(fileInfo))
        {
            ReportPatternConfigRejected(state, path, "symbolic links and reparse points are not supported");
            return null;
        }

        if (fileInfo.Length > MaxPatternConfigBytes)
        {
            observeInput?.Invoke(path, null, fileInfo.Length);
            ReportPatternConfigRejected(state, path, $"file is too large ({fileInfo.Length} bytes; maximum {MaxPatternConfigBytes})");
            return null;
        }

        return OperatingSystem.IsWindows()
            ? TryReadWindowsPatternConfigText(state, path, observeInput)
            : TryReadUnixPatternConfigText(state, path, observeInput);
    }

    private static bool TryReadNextPatternConfigLine(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> line)
    {
        if (remaining.IsEmpty)
        {
            line = default;
            return false;
        }

        var lineBreakIndex = remaining.IndexOfAny('\r', '\n');
        if (lineBreakIndex < 0)
        {
            line = remaining;
            remaining = default;
            return true;
        }

        line = remaining[..lineBreakIndex];
        var nextIndex = lineBreakIndex + 1;
        if (remaining[lineBreakIndex] == '\r' && nextIndex < remaining.Length && remaining[nextIndex] == '\n')
            nextIndex++;
        remaining = remaining[nextIndex..];
        return true;
    }

    private static ReadOnlySpan<char> TrimPatternConfigListMarker(ReadOnlySpan<char> line)
    {
        while (!line.IsEmpty && line[0] == '-')
            line = line[1..];
        return line.Trim();
    }

    private static string? TryReadWindowsPatternConfigText(
        PatternWorkspaceState state,
        string path,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput)
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
            observeInput?.Invoke(path, null, null);
            ReportPatternConfigRejected(state, path, $"could not open safely (errno {Marshal.GetLastPInvokeError()})");
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            observeInput?.Invoke(path, null, null);
            ReportPatternConfigRejected(state, path, $"could not inspect file handle (errno {Marshal.GetLastPInvokeError()})");
            return null;
        }

        var attributes = (FileAttributes)info.FileAttributes;
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            observeInput?.Invoke(path, null, null);
            ReportPatternConfigRejected(state, path, "path is not a regular file");
            return null;
        }

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (size > MaxPatternConfigBytes)
        {
            observeInput?.Invoke(path, null, size);
            ReportPatternConfigRejected(state, path, $"file is too large ({size} bytes; maximum {MaxPatternConfigBytes})");
            return null;
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 8192, isAsync: false);
        return TryReadBoundedPatternConfigText(state, path, stream, observeInput, size);
    }

    private static string? TryReadUnixPatternConfigText(
        PatternWorkspaceState state,
        string path,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput)
    {
        var fd = UnixOpen(path, GetUnixOpenFlags());
        if (fd < 0)
        {
            observeInput?.Invoke(path, null, null);
            ReportPatternConfigRejected(state, path, $"could not open safely (errno {Marshal.GetLastPInvokeError()})");
            return null;
        }

        try
        {
            if (!TryGetUnixFileType(fd, out var mode) || !IsRegularUnixFile(mode))
            {
                observeInput?.Invoke(path, null, null);
                ReportPatternConfigRejected(state, path, "path is not a regular file");
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
                    observeInput?.Invoke(path, null, null);
                    ReportPatternConfigRejected(state, path, $"could not read safely (errno {Marshal.GetLastPInvokeError()})");
                    return null;
                }

                stream.Write(buffer, 0, (int)bytesRead);
            }

            return ValidatePatternConfigText(
                state,
                path,
                stream,
                observeInput,
                observedLength: null);
        }
        finally
        {
            _ = UnixClose(fd);
        }
    }

    private static string? TryReadBoundedPatternConfigText(
        PatternWorkspaceState state,
        string path,
        Stream stream,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput,
        long? observedLength = null)
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

        if (!observedLength.HasValue && stream.CanSeek)
        {
            try
            {
                observedLength = stream.Length;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
            {
                // The bounded bytes still make a successful small read observable.
            }
        }

        return ValidatePatternConfigText(state, path, output, observeInput, observedLength);
    }

    private static string? ValidatePatternConfigText(
        PatternWorkspaceState state,
        string path,
        MemoryStream stream,
        Action<string, ReadOnlyMemory<byte>?, long?>? observeInput,
        long? observedLength)
    {
        if (stream.Length <= MaxPatternConfigBytes)
        {
            if (observeInput != null)
            {
                if (stream.TryGetBuffer(out var segment) && segment.Array is { } buffer)
                    observeInput(path, new ReadOnlyMemory<byte>(buffer, segment.Offset, segment.Count), segment.Count);
                else
                {
                    var content = stream.ToArray();
                    observeInput(path, content, content.Length);
                }
            }
            return DecodePatternConfigText(stream);
        }

        observeInput?.Invoke(path, null, observedLength ?? stream.Length);
        ReportPatternConfigRejected(state, path, $"file is too large (more than {MaxPatternConfigBytes} bytes)");
        return null;
    }

    private static string DecodePatternConfigText(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var segment) && segment.Array is { } buffer)
            return Encoding.UTF8.GetString(buffer, segment.Offset, segment.Count);

        return Encoding.UTF8.GetString(stream.ToArray());
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

    private enum PatternScalarReadResult
    {
        Missing,
        Empty,
        TooLong,
        Success,
    }

    private static bool TryReadScalar(ReadOnlySpan<char> line, string key, out string value)
        => TryReadScalar(line, key, int.MaxValue, out value, out _) == PatternScalarReadResult.Success;

    private static PatternScalarReadResult TryReadScalar(
        ReadOnlySpan<char> line,
        string key,
        int maxLength,
        out string value,
        out int scalarLength)
    {
        value = string.Empty;
        scalarLength = 0;
        if (line.Length <= key.Length || line[key.Length] != ':')
            return PatternScalarReadResult.Missing;

        if (!line.StartsWith(key.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return PatternScalarReadResult.Missing;

        var scalar = TrimScalarQuotes(line[(key.Length + 1)..].Trim());
        if (scalar.IsEmpty)
            return PatternScalarReadResult.Empty;

        value = scalar.ToString().Replace("\\\\", "\\", StringComparison.Ordinal);
        scalarLength = value.Length;
        if (scalarLength == 0)
            return PatternScalarReadResult.Empty;
        return scalarLength > maxLength
            ? PatternScalarReadResult.TooLong
            : PatternScalarReadResult.Success;
    }

    private static ReadOnlySpan<char> TrimScalarQuotes(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty && (value[0] == '"' || value[0] == '\''))
            value = value[1..];
        while (!value.IsEmpty && (value[^1] == '"' || value[^1] == '\''))
            value = value[..^1];
        return value;
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
}
