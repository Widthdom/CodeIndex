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

    private readonly record struct ConfigPathCandidate(string Path, bool IsUserConfig);

    private readonly record struct ConfigPathStamp(
        string Path,
        bool IsUserConfig,
        bool Exists,
        DateTime LastWriteTimeUtc,
        long Length);

    private sealed record EffectiveMapCacheEntry(
        ConfigPathStamp[] Stamps,
        IReadOnlyDictionary<string, string> Map);

    internal static IReadOnlyDictionary<string, string> LoadEffectiveMap(string? startPath = null)
    {
        var startDirectory = ResolveStartDirectory(startPath);
        var candidates = EnumerateConfigPathCandidates(startDirectory).ToArray();
        var stamps = GetConfigPathStamps(candidates);

        lock (EffectiveMapCacheLock)
        {
            if (EffectiveMapCache.TryGetValue(startDirectory, out var cached)
                && ConfigPathStampsEqual(cached.Stamps, stamps))
            {
                return cached.Map;
            }
        }

        var map = LoadEffectiveMapFromPaths(SelectEffectiveConfigPaths(stamps), ReportWarningOnce);

        lock (EffectiveMapCacheLock)
        {
            if (EffectiveMapCache.Count >= MaxEffectiveMapCacheEntries)
                EffectiveMapCache.Clear();
            EffectiveMapCache[startDirectory] = new EffectiveMapCacheEntry(stamps, map);
        }

        return map;
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
            LoadInto(path, map, reportWarning);
        return map;
    }

    private static IEnumerable<ConfigPathCandidate> EnumerateConfigPathCandidates(string startDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return new ConfigPathCandidate(Path.Combine(home, ".config", "cdidx", "langmap.yaml"), IsUserConfig: true);

        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, WorkspaceFileName);
            yield return new ConfigPathCandidate(candidate, IsUserConfig: false);

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }
    }

    private static ConfigPathStamp[] GetConfigPathStamps(IReadOnlyList<ConfigPathCandidate> candidates)
    {
        var stamps = new ConfigPathStamp[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            stamps[i] = GetConfigPathStamp(candidates[i]);
        return stamps;
    }

    private static ConfigPathStamp GetConfigPathStamp(ConfigPathCandidate candidate)
    {
        try
        {
            var info = new FileInfo(candidate.Path);
            return info.Exists
                ? new ConfigPathStamp(candidate.Path, candidate.IsUserConfig, Exists: true, info.LastWriteTimeUtc, info.Length)
                : new ConfigPathStamp(candidate.Path, candidate.IsUserConfig, Exists: false, DateTime.MinValue, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ConfigPathStamp(candidate.Path, candidate.IsUserConfig, Exists: false, DateTime.MinValue, 0);
        }
    }

    private static IEnumerable<string> SelectEffectiveConfigPaths(IReadOnlyList<ConfigPathStamp> stamps)
    {
        foreach (var stamp in stamps)
        {
            if (stamp.IsUserConfig)
            {
                if (stamp.Exists)
                    yield return stamp.Path;
                continue;
            }

            if (stamp.Exists)
            {
                yield return stamp.Path;
                yield break;
            }
        }
    }

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
        Action<string, string?>? reportWarning)
    {
        if (!File.Exists(path))
            return;

        if (!TryReadBoundedUtf8Lines(path, out var lines, out var skippedReason))
        {
            reportWarning?.Invoke(
                $"Skipped language-map override file {DiagnosticSanitizer.ForPath(path)} because {DiagnosticSanitizer.ForMessage(skippedReason)}.",
                $"{path}\n{skippedReason}");
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

    private static bool TryReadBoundedUtf8Lines(string path, out IReadOnlyList<string> lines, out string skippedReason)
    {
        var success = BoundedLineReader.TryReadUtf8File(
            path,
            MaxOverrideFileBytes,
            MaxOverrideFileLines,
            MaxOverrideLineChars,
            out lines,
            out var failure,
            OpenOverrideFileForTesting);
        skippedReason = success ? string.Empty : failure.Reason;
        return success;
    }

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
