namespace CodeIndex.Indexer;

internal static class DotnetHostPathResolver
{
    private static readonly AsyncLocal<IReadOnlyList<string>?> TrustedDotnetHostCandidatesOverrideValue = new();

    internal static IReadOnlyList<string>? TrustedDotnetHostCandidatesOverride
    {
        get => TrustedDotnetHostCandidatesOverrideValue.Value;
        set => TrustedDotnetHostCandidatesOverrideValue.Value = value;
    }

    internal static string? Resolve(string? currentProcessPath)
    {
        if (TryNormalizeDotnetHostPath(currentProcessPath, out var normalized))
            return normalized;

        foreach (var candidate in TrustedDotnetHostCandidatesOverrideValue.Value ?? EnumerateTrustedDotnetHostCandidates())
        {
            if (TryNormalizeDotnetHostPath(candidate, out normalized))
                return normalized;
        }

        return null;
    }

    internal static bool IsDotnetHostPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateTrustedDotnetHostCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "dotnet", "dotnet.exe");

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
                yield return Path.Combine(programFilesX86, "dotnet", "dotnet.exe");

            yield break;
        }

        yield return "/usr/bin/dotnet";
        yield return "/usr/share/dotnet/dotnet";
    }

    private static bool TryNormalizeDotnetHostPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !IsDotnetHostPath(path))
            return false;

        try
        {
            if (!Path.IsPathFullyQualified(path))
                return false;

            normalized = Path.GetFullPath(path);
            return File.Exists(LongPath.EnsureWindowsPrefix(normalized));
        }
        catch
        {
            normalized = string.Empty;
            return false;
        }
    }
}
