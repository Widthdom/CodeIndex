using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static string BuildFoldBackfillCommand(string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx backfill-fold";

        return $"cdidx backfill-fold --db {QuoteCommandArgument(ResolveWritableDbPathOrPlaceholder(dbPath))}";
    }

    private static string BuildCSharpCanonicalNameRepairCommand(DbReader reader, QueryCommandOptions options)
    {
        var status = reader.GetStatus(includeDatabaseSizeAttribution: false);
        WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit);
        return BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit);
    }

    private static string BuildCSharpCanonicalNameRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit);

    private static string BuildSqlGraphContractRepairCommand(DbReader reader, QueryCommandOptions options)
    {
        var status = reader.GetStatus(includeDatabaseSizeAttribution: false);
        WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit);
        return BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit);
    }

    private static string BuildSqlGraphContractRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit);

    private static string BuildHotspotFamilyRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit, rebuild: true);

    private static string BuildFoldRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit, rebuild: true);

    private static string BuildReindexRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit, bool rebuild = false)
    {
        var rebuildSuffix = rebuild ? " --rebuild" : string.Empty;
        if (!dbPathExplicit)
            return $"cdidx index .{rebuildSuffix}";

        var resolvedDbPath = ResolveWritableDbPathOrPlaceholder(dbPath);
        var targetProject = string.IsNullOrWhiteSpace(projectRoot)
            ? "<projectPath>"
            : QuoteCommandArgument(projectRoot);
        return $"cdidx index {targetProject} --db {QuoteCommandArgument(resolvedDbPath)}{rebuildSuffix}";
    }

    private static string ResolveWritableDbPathOrPlaceholder(string dbPath)
        => DbPathResolver.TryResolveWritableMutationDbPath(dbPath, out var writableDbPath)
            ? writableDbPath
            : "<writable-db-path>";

    private static string QuoteCommandArgument(string value)
    {
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            return value;

        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return QuoteCommandToken(fullPath);
    }

    private static string RenderStatusRepairCommand(StatusRepairCommand command)
        => ExcerptRecoveryCommandFormatter.RenderDisplayCommand(
            new[] { command.Name }.Concat(command.Args).ToArray(),
            OperatingSystem.IsWindows() ? RecoveryCommandShell.PowerShell : RecoveryCommandShell.PosixSh);

    private static string QuoteCommandToken(string value)
    {
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            return value;

        return value.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
