namespace CodeIndex.Cli;

internal static class CliCommandCatalog
{
    internal static readonly string[] Commands =
    [
        "index", "hooks", "backfill-fold", "optimize", "vacuum", "search", "recipes", "audit", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status", "workspace", "config", "upgrade", "validate-config",
        "doctor", "db", "diff", "report", "validate", "deps", "impact", "unused", "hotspots", "suggestions", "export", "import", "languages", "batch", "mcp", "lsp", "completions", "license", "help",
    ];

    internal static readonly IReadOnlyDictionary<string, string> CommandAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["refs"] = "references",
            ["stats"] = "status",
            ["fold"] = "backfill-fold",
        };

    internal static string NormalizePublicCommandName(string command) =>
        CommandAliases.TryGetValue(command, out var canonical) ? canonical : command;

    internal static bool TryResolvePublicCommand(string input, out string command)
    {
        command = NormalizePublicCommandName(input);
        return Commands.Contains(command, StringComparer.Ordinal);
    }

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
        ("recipes", ["list"]),
        ("suggestions", ["list", "show", "export", "add", "update", "delete"]),
        ("export", ["ctags"]),
    ];

    // These command families accept their parent form as the default operation, so completion
    // must offer parent flags alongside nested verbs at the first argument.
    // 親 command 自体が default operation として有効な family。最初の引数では nested verb
    // だけでなく親 command の flag も同時に補完する。
    internal static readonly IReadOnlySet<string> OptionalSubcommandCommands =
        new HashSet<string>(["recipes", "suggestions"], StringComparer.Ordinal);

    internal static bool HasOptionalSubcommand(string command) =>
        OptionalSubcommandCommands.Contains(command);

    internal static IReadOnlyList<string> GetSubcommands(string command)
    {
        foreach (var (candidate, subcommands) in CommandSubcommands)
        {
            if (string.Equals(candidate, command, StringComparison.Ordinal))
                return subcommands;
        }

        return [];
    }

    internal static string NormalizeSubcommandName(string command, string subcommand) =>
        command == "db" && subcommand == "--integrity-check"
            ? "integrity"
            : subcommand;
}
