using CodeIndex.Database;

namespace CodeIndex.Cli;

internal static class ExcerptRecoveryCommandFormatter
{
    public static void ApplyDbPath(FileExcerptResult excerpt, string dbPath)
    {
        ApplyDbPath(excerpt.ContentRecovery, excerpt.Path, dbPath);
    }

    public static void ApplyDbPath(ExcerptRecoveryHint? recovery, string path, string dbPath)
    {
        if (recovery is null)
            return;

        recovery.Command = BuildCommand(path, recovery.StartLine, recovery.EndLine, dbPath);
    }

    private static string BuildCommand(string path, int startLine, int endLine, string dbPath)
    {
        var dbOption = string.IsNullOrWhiteSpace(dbPath)
            ? string.Empty
            : $" --db {QuoteShellArgument(NormalizeDbPath(dbPath))}";
        return $"cdidx excerpt {QuoteShellArgument(path)}{dbOption} --start {startLine} --end {endLine} --max-line-width 0 --json";
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

    private static string QuoteShellArgument(string value)
    {
        if (!string.IsNullOrEmpty(value) && value.All(IsSafeShellArgumentChar))
            return value;

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool IsSafeShellArgumentChar(char c)
        => char.IsLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ':';
}
