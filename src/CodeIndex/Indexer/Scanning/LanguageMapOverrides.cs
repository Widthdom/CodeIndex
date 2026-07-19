using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer;

internal static class LanguageMapOverrides
{
    internal const string WorkspaceFileName = ".cdidx-langmap.yaml";
    private const int MaxOverrideFileBytes = 128 * 1024;
    private const int MaxOverrideFileLines = 16384;
    private const int MaxOverrideLineChars = 16 * 1024;
    private const int MaxOverrideEntries = 4096;
    private const int MaxOverridePatterns = 8192;
    private const int MaxEffectiveMapCacheEntries = 4096;
    private static readonly object WarningLock = new();
    private static readonly HashSet<string> ReportedWarnings = new(StringComparer.Ordinal);
    private static readonly object EffectiveMapCacheLock = new();
    private static readonly Dictionary<string, EffectiveMapCacheEntry> EffectiveMapCache = new(StringComparer.Ordinal);

    internal static Func<string, Stream>? OpenOverrideFileForTesting { get; set; }
    internal static Action<string>? ConfigPathStampProbeForTesting { get; set; }

    internal enum ConfigProbeStatus
    {
        Missing,
        Present,
        ProbeFailed,
    }

    internal sealed record Diagnostic(
        string Code,
        string Config,
        string Reason,
        bool BlocksParentFallback);

    internal sealed record LoadResult(
        IReadOnlyDictionary<string, string> Map,
        IReadOnlyList<Diagnostic> Diagnostics);

    private readonly record struct ConfigPathCandidate(string Path, bool IsUserConfig);

    private readonly record struct ConfigPathStamp(
        string Path,
        bool IsUserConfig,
        ConfigProbeStatus Status,
        DateTime LastWriteTimeUtc,
        long Length,
        string? FailureReason = null);

    private sealed record EffectiveMapCacheEntry(
        ConfigPathStamp[] Stamps,
        LoadResult Result);

    internal static IReadOnlyDictionary<string, string> LoadEffectiveMap(string? startPath = null)
    {
        return LoadEffectiveMapWithDiagnostics(startPath).Map;
    }

    internal static IReadOnlyDictionary<string, string> LoadEffectiveMapFromDirectory(string? startDirectory)
        => LoadEffectiveMapFromDirectoryWithDiagnostics(startDirectory).Map;

    internal static LoadResult LoadEffectiveMapWithDiagnostics(string? startPath = null)
        => LoadEffectiveMapFromDirectoryWithDiagnostics(ResolveStartDirectory(startPath));

    internal static LoadResult LoadEffectiveMapFromDirectoryWithDiagnostics(string? startDirectory)
    {
        startDirectory = NormalizeStartDirectory(startDirectory);
        EffectiveMapCacheEntry? cached;

        lock (EffectiveMapCacheLock)
        {
            EffectiveMapCache.TryGetValue(startDirectory, out cached);
        }

        if (cached != null)
        {
            var refreshedStamps = RefreshConfigPathStamps(cached.Stamps);
            if (ConfigPathStampsEqual(cached.Stamps, refreshedStamps))
                return cached.Result;
        }

        var candidates = CreateConfigPathCandidates(startDirectory);
        var stamps = GetConfigPathStamps(candidates);
        var result = LoadEffectiveMapFromStamps(stamps, ReportWarningOnce);

        lock (EffectiveMapCacheLock)
        {
            if (EffectiveMapCache.Count >= MaxEffectiveMapCacheEntries)
                EffectiveMapCache.Clear();
            EffectiveMapCache[startDirectory] = new EffectiveMapCacheEntry(stamps, result);
        }

        return result;
    }

    internal static string NormalizeStartDirectory(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return Environment.CurrentDirectory;
        return Path.IsPathFullyQualified(startDirectory)
            ? startDirectory
            : Path.GetFullPath(startDirectory);
    }

    internal static void ClearEffectiveMapCacheForTesting()
    {
        lock (EffectiveMapCacheLock)
            EffectiveMapCache.Clear();
    }

    internal static IReadOnlyDictionary<string, string> LoadEffectiveMapFromPathsForTesting(
        IEnumerable<string> configPaths,
        Action<string>? reportWarning = null)
        => LoadEffectiveMapFromPaths(
            configPaths,
            reportWarning == null ? null : (message, _) => reportWarning(message));

    private static IReadOnlyDictionary<string, string> LoadEffectiveMapFromPaths(
        IEnumerable<string> configPaths,
        Action<string, string?>? reportWarning)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in configPaths)
            LoadInto(path, map, reportWarning, diagnostics: null, blocksParentFallback: false);
        return map;
    }

    private static LoadResult LoadEffectiveMapFromStamps(
        IReadOnlyList<ConfigPathStamp> stamps,
        Action<string, string?>? reportWarning)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<Diagnostic>();
        foreach (var stamp in stamps)
        {
            if (stamp.Status == ConfigProbeStatus.ProbeFailed)
            {
                var reason = stamp.FailureReason ?? "probe_failed";
                diagnostics.Add(new Diagnostic(
                    "language_map_probe_failed",
                    DiagnosticSanitizer.ForPath(stamp.Path),
                    reason,
                    BlocksParentFallback: !stamp.IsUserConfig));
                reportWarning?.Invoke(
                    $"Skipped language-map override file {DiagnosticSanitizer.ForPath(stamp.Path)} because its metadata could not be read ({reason}).",
                    $"{stamp.Path}\nprobe\n{reason}");
                if (!stamp.IsUserConfig)
                    break;
                continue;
            }

            if (stamp.IsUserConfig)
            {
                if (stamp.Status == ConfigProbeStatus.Present)
                    LoadInto(stamp.Path, map, reportWarning, diagnostics, blocksParentFallback: false);
                continue;
            }

            if (stamp.Status == ConfigProbeStatus.Missing)
                continue;

            LoadInto(stamp.Path, map, reportWarning, diagnostics, blocksParentFallback: true);
            break;
        }

        return new LoadResult(map, diagnostics);
    }

    private static List<ConfigPathCandidate> CreateConfigPathCandidates(string startDirectory)
    {
        var candidates = new List<ConfigPathCandidate>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            candidates.Add(new ConfigPathCandidate(Path.Combine(home, ".config", "cdidx", "langmap.yaml"), IsUserConfig: true));

        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, WorkspaceFileName);
            candidates.Add(new ConfigPathCandidate(candidate, IsUserConfig: false));

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        return candidates;
    }

    private static ConfigPathStamp[] GetConfigPathStamps(IReadOnlyList<ConfigPathCandidate> candidates)
    {
        var stamps = new ConfigPathStamp[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            stamps[i] = GetConfigPathStamp(candidates[i]);
        return stamps;
    }

    private static ConfigPathStamp[] RefreshConfigPathStamps(IReadOnlyList<ConfigPathStamp> cachedStamps)
    {
        var stamps = new ConfigPathStamp[cachedStamps.Count];
        for (var i = 0; i < cachedStamps.Count; i++)
        {
            var cached = cachedStamps[i];
            stamps[i] = GetConfigPathStamp(new ConfigPathCandidate(cached.Path, cached.IsUserConfig));
        }

        return stamps;
    }

    private static ConfigPathStamp GetConfigPathStamp(ConfigPathCandidate candidate)
    {
        try
        {
            ConfigPathStampProbeForTesting?.Invoke(candidate.Path);
            var ioPath = LongPath.EnsureWindowsPrefix(candidate.Path);
            var attributes = File.GetAttributes(ioPath);
            if ((attributes & FileAttributes.Directory) != 0
                || FileSystemBoundary.IsSymlinkOrReparsePoint(attributes)
                || FileSystemBoundary.IsDevice(attributes))
            {
                return ProbeFailedStamp(candidate, "not_regular_file");
            }

            var info = new FileInfo(candidate.Path);
            info.Refresh();
            return info.Exists
                ? new ConfigPathStamp(candidate.Path, candidate.IsUserConfig, ConfigProbeStatus.Present, info.LastWriteTimeUtc, info.Length)
                : ProbeFailedStamp(candidate, "io_error");
        }
        catch (FileNotFoundException)
        {
            return HasDanglingLink(candidate.Path)
                ? ProbeFailedStamp(candidate, "not_regular_file")
                : MissingStamp(candidate);
        }
        catch (DirectoryNotFoundException)
        {
            return HasDanglingLink(candidate.Path)
                ? ProbeFailedStamp(candidate, "not_regular_file")
                : MissingStamp(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return ProbeFailedStamp(candidate, GetFailureReason(ex));
        }
    }

    private static ConfigPathStamp MissingStamp(ConfigPathCandidate candidate)
        => new(candidate.Path, candidate.IsUserConfig, ConfigProbeStatus.Missing, DateTime.MinValue, 0);

    private static ConfigPathStamp ProbeFailedStamp(ConfigPathCandidate candidate, string reason)
        => new(
            candidate.Path,
            candidate.IsUserConfig,
            ConfigProbeStatus.ProbeFailed,
            DateTime.MinValue,
            0,
            reason);

    private static bool HasDanglingLink(string path)
    {
        try
        {
            return !string.IsNullOrEmpty(new FileInfo(path).LinkTarget)
                || !string.IsNullOrEmpty(new DirectoryInfo(path).LinkTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static ConfigProbeStatus ProbeWorkspaceConfigFile(string directory)
        => GetConfigPathStamp(new ConfigPathCandidate(
            Path.Combine(directory, WorkspaceFileName),
            IsUserConfig: false)).Status;

    private static bool ConfigPathStampsEqual(IReadOnlyList<ConfigPathStamp> left, IReadOnlyList<ConfigPathStamp> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }

    private static string ResolveStartDirectory(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return Environment.CurrentDirectory;

        var fullPath = Path.GetFullPath(startPath);
        return Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
    }

    private static void LoadInto(
        string path,
        Dictionary<string, string> target,
        Action<string, string?>? reportWarning,
        ICollection<Diagnostic>? diagnostics,
        bool blocksParentFallback)
    {
        if (!TryReadBoundedUtf8Lines(path, out var lines, out var failure))
        {
            reportWarning?.Invoke(
                $"Skipped language-map override file {DiagnosticSanitizer.ForPath(path)} because {DiagnosticSanitizer.ForMessage(failure.Reason)}.",
                $"{path}\n{failure.Reason}");
            diagnostics?.Add(new Diagnostic(
                failure.Kind == BoundedTextFileReadFailureKind.ReadFailed
                    ? "language_map_read_failed"
                    : "language_map_rejected",
                DiagnosticSanitizer.ForPath(path),
                GetFailureReason(failure),
                blocksParentFallback));
            return;
        }

        string? pendingExtension = null;
        var entryCount = 0;
        var patternCount = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (TryReadScalar(line.TrimStart('-').Trim(), "extension", out var value))
            {
                if (patternCount >= MaxOverridePatterns)
                {
                    reportWarning?.Invoke(
                        $"Ignored remaining language-map override patterns in {DiagnosticSanitizer.ForPath(path)} because the pattern count exceeds {MaxOverridePatterns}.",
                        $"{path}\npattern-count");
                    return;
                }

                pendingExtension = NormalizeExtension(value);
                patternCount++;
                continue;
            }

            if (TryReadScalar(line.TrimStart('-').Trim(), "language", out value) && pendingExtension != null)
            {
                if (entryCount >= MaxOverrideEntries)
                {
                    reportWarning?.Invoke(
                        $"Ignored remaining language-map override entries in {DiagnosticSanitizer.ForPath(path)} because the loaded entry count exceeds {MaxOverrideEntries}.",
                        $"{path}\nentry-count");
                    return;
                }

                target[pendingExtension] = value.Trim().ToLowerInvariant();
                pendingExtension = null;
                entryCount++;
            }
        }
    }

    private static bool TryReadBoundedUtf8Lines(
        string path,
        out IReadOnlyList<string> lines,
        out BoundedTextFileReadFailure failure)
    {
        var success = BoundedLineReader.TryReadUtf8File(
            path,
            MaxOverrideFileBytes,
            MaxOverrideFileLines,
            MaxOverrideLineChars,
            out lines,
            out failure,
            OpenOverrideFileForTesting);
        return success;
    }

    private static string GetFailureReason(BoundedTextFileReadFailure failure)
        => failure.Kind switch
        {
            BoundedTextFileReadFailureKind.BytesExceeded => "bytes_exceeded",
            BoundedTextFileReadFailureKind.LinesExceeded => "lines_exceeded",
            BoundedTextFileReadFailureKind.LineLengthExceeded => "line_length_exceeded",
            BoundedTextFileReadFailureKind.InvalidUtf8 => "invalid_utf8",
            BoundedTextFileReadFailureKind.ReadFailed => failure.ExceptionType switch
            {
                nameof(UnauthorizedAccessException) => "access_denied",
                nameof(NotSupportedException) => "unsupported_path",
                _ => "io_error",
            },
            _ => "unknown",
        };

    private static string GetFailureReason(Exception exception)
        => exception switch
        {
            UnauthorizedAccessException => "access_denied",
            System.Security.SecurityException => "access_denied",
            NotSupportedException => "unsupported_path",
            ArgumentException => "invalid_path",
            _ => "io_error",
        };

    private static void ReportWarningOnce(string message, string? dedupeKey)
    {
        lock (WarningLock)
        {
            if (!ReportedWarnings.Add(dedupeKey ?? message))
                return;
        }

        CommandErrorWriter.WriteStderr("cdidx: warning: " + message);
    }

    private static bool TryReadScalar(string line, string key, out string value)
    {
        value = string.Empty;
        var prefix = key + ":";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        value = line[prefix.Length..].Trim().Trim('"', '\'');
        return value.Length > 0;
    }

    private static string NormalizeExtension(string extension)
    {
        extension = extension.Trim().ToLowerInvariant();
        return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
    }
}
