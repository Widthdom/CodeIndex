using System.Text;

namespace CodeIndex.Indexer;

internal static class LanguageMapOverrides
{
    internal const string WorkspaceFileName = ".cdidx-langmap.yaml";
    private const int MaxOverrideFileBytes = 128 * 1024;
    private const int MaxOverrideFileLines = 16384;
    private const int MaxOverrideEntries = 4096;
    private static readonly object WarningLock = new();
    private static readonly HashSet<string> ReportedWarnings = new(StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> LoadEffectiveMap(string? startPath = null)
        => LoadEffectiveMapFromPaths(EnumerateConfigPaths(startPath), ReportWarningOnce);

    internal static IReadOnlyDictionary<string, string> LoadEffectiveMapFromPathsForTesting(
        IEnumerable<string> configPaths,
        Action<string>? reportWarning = null)
        => LoadEffectiveMapFromPaths(configPaths, reportWarning);

    private static IReadOnlyDictionary<string, string> LoadEffectiveMapFromPaths(
        IEnumerable<string> configPaths,
        Action<string>? reportWarning)
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
        Action<string>? reportWarning)
    {
        if (!File.Exists(path))
            return;

        if (!TryReadBoundedUtf8Lines(path, out var lines, out var skippedReason))
        {
            reportWarning?.Invoke($"Skipped language-map override file {path} because {skippedReason}.");
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
                    reportWarning?.Invoke($"Ignored remaining language-map override entries in {path} because the loaded entry count exceeds {MaxOverrideEntries}.");
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
        lines = [];
        skippedReason = string.Empty;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                useAsync: false);

            if (stream.Length > MaxOverrideFileBytes)
            {
                skippedReason = $"it exceeds {MaxOverrideFileBytes} bytes";
                return false;
            }

            using var accumulator = new MemoryStream((int)Math.Min(stream.Length, MaxOverrideFileBytes));
            var buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxOverrideFileBytes)
                {
                    skippedReason = $"it exceeds {MaxOverrideFileBytes} bytes";
                    return false;
                }

                accumulator.Write(buffer, 0, read);
            }

            var text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(accumulator.ToArray());
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];

            var result = new List<string>();
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (result.Count >= MaxOverrideFileLines)
                {
                    skippedReason = $"it exceeds {MaxOverrideFileLines} lines";
                    return false;
                }

                result.Add(line);
            }

            lines = result;
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void ReportWarningOnce(string message)
    {
        lock (WarningLock)
        {
            if (!ReportedWarnings.Add(message))
                return;
        }

        Console.Error.WriteLine("cdidx: warning: " + message);
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
