namespace CodeIndex.Indexer;

internal static class DotnetHostPathResolver
{
    internal static string? Resolve(string? currentProcessPath)
        => TryNormalizeDotnetHostPath(currentProcessPath, out var normalized)
            ? normalized
            : null;

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
            return File.Exists(normalized);
        }
        catch
        {
            normalized = string.Empty;
            return false;
        }
    }
}
