using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
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

            var configText = TryReadPatternConfigText(path);
            if (configText == null)
                return;

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
                {
                    ReportPatternConfigRejected(path, $"language scalar is too long ({scalarLength} characters; maximum {MaxPatternLanguageLength})");
                    return;
                }

                if (scalarResult == PatternScalarReadResult.Success)
                {
                    language = NormalizePluginLanguage(value);
                }
                else
                {
                    scalarResult = TryReadScalar(itemLine, "extension", MaxPatternExtensionLength, out value, out scalarLength);
                    if (scalarResult == PatternScalarReadResult.TooLong)
                    {
                        ReportPatternConfigRejected(path, $"extension scalar is too long ({scalarLength} characters; maximum {MaxPatternExtensionLength})");
                        return;
                    }

                    if (scalarResult == PatternScalarReadResult.Success)
                    {
                        var extension = NormalizePluginExtension(value) ?? value;
                        if (extension.Length > MaxPatternExtensionLength)
                        {
                            ReportPatternConfigRejected(path, $"extension scalar is too long ({extension.Length} characters; maximum {MaxPatternExtensionLength})");
                            return;
                        }

                        extensions.Add(extension);
                    }
                    else
                    {
                        scalarResult = TryReadScalar(itemLine, "kind", MaxPatternKindLength, out value, out scalarLength);
                        if (scalarResult == PatternScalarReadResult.TooLong)
                        {
                            ReportPatternConfigRejected(path, $"kind scalar is too long ({scalarLength} characters; maximum {MaxPatternKindLength})");
                            return;
                        }

                        if (scalarResult == PatternScalarReadResult.Success)
                        {
                            pendingKind = value.Trim();
                        }
                        else if (TryReadScalar(itemLine, "regex", out value) && pendingKind != null)
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
                                regex = Regex.CreateExtractionRegex(
                                    value,
                                    RegexOptions.Compiled,
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

    private static string? TryReadPatternConfigText(string path)
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

        return Encoding.UTF8.GetString(bytes);
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
