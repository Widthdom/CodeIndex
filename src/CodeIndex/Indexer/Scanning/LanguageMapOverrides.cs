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
    private static readonly object WarningLock = new();
    private static readonly HashSet<string> ReportedWarnings = new(StringComparer.Ordinal);

    internal static Func<string, Stream>? OpenOverrideFileForTesting { get; set; }

    internal static IReadOnlyDictionary<string, string> LoadEffectiveMap(string? startPath = null)
        => LoadEffectiveMapFromPaths(EnumerateConfigPaths(startPath), ReportWarningOnce);

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

    private static IEnumerable<string> EnumerateConfigPaths(string? startPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".config", "cdidx", "langmap.yaml");

        var directory = ResolveStartDirectory(startPath);
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, WorkspaceFileName);
            if (File.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }
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
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (TryReadScalar(line.TrimStart('-').Trim(), "extension", out var value))
            {
                pendingExtension = NormalizeExtension(value);
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
