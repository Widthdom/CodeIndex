namespace CodeIndex.Cli;

internal static class CliCommandCatalog
{
    internal static readonly string[] Commands =
    [
        "index", "hooks", "backfill-fold", "optimize", "vacuum", "search", "recipes", "audit", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status", "workspace", "config", "upgrade", "validate-config",
        "doctor", "db", "diff", "report", "validate", "deps", "impact", "unused", "hotspots", "suggestions", "export", "import", "languages", "batch", "mcp", "lsp", "completions", "license",
    ];

    /// <summary>
    /// Authoritative nested-command inventory shared by help routing and every shell
    /// completion renderer. Keep nested command discovery here instead of duplicating
    /// lists in individual output surfaces (#4571).
    /// help routing と全 shell completion renderer が共有する nested command の正本。
    /// 個別の出力 surface に一覧を複製しない (#4571)。
    /// </summary>
    internal static readonly (string Command, string[] Subcommands)[] CommandSubcommands =
    [
        ("hooks", ["install", "uninstall", "status"]),
        ("workspace", ["list", "status", "use", "current", "clear", "deactivate"]),
        ("config", ["show"]),
        ("db", ["integrity", "schema", "prune", "checkpoint", "checkpoints", "restore", "restore-backups"]),
    ];

    internal static IReadOnlyList<string> GetSubcommands(string command)
    {
        foreach (var (candidate, subcommands) in CommandSubcommands)
        {
            if (string.Equals(candidate, command, StringComparison.Ordinal))
                return subcommands;
        }

        return [];
    }
}
