namespace CodeIndex.Cli;

internal static class CliCommandCatalog
{
    internal static IReadOnlyList<string> PublicCommandNames => CliCommandMetadata.PublicCommandNames;

    // Authoritative allowlist for `cdidx batch`. Keep the side-effect boundary in the
    // shared command catalog so adding a top-level command cannot make it batch-dispatchable
    // merely by adding a switch arm (#4582).
    // `cdidx batch` の副作用なし command allowlist の正本。top-level command の追加だけで
    // batch dispatch が暗黙に許可されないよう、共有 command catalog で境界を管理する (#4582)。
    internal static readonly string[] BatchReadOnlyCommands =
    [
        "search", "recipes", "audit", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "languages", "impact", "deps", "unused", "hotspots",
    ];

    internal static bool IsBatchReadOnlyCommand(string command) =>
        BatchReadOnlyCommands.Contains(command, StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> CommandAliases =>
        CliCommandMetadata.CommandAliases;

    internal static string NormalizePublicCommandName(string command) =>
        CommandAliases.TryGetValue(command, out var canonical) ? canonical : command;

    internal static bool TryResolvePublicCommand(string input, out string command)
    {
        command = NormalizePublicCommandName(input);
        return PublicCommandNames.Contains(command, StringComparer.Ordinal);
    }

    /// <summary>
    /// Authoritative nested-command inventory shared by help routing and every shell
    /// completion renderer. Keep nested command discovery here instead of duplicating
    /// lists in individual output surfaces (#4571).
    /// help routing と全 shell completion renderer が共有する nested command の正本。
    /// 個別の出力 surface に一覧を複製しない (#4571)。
    /// </summary>
    internal static IReadOnlyList<(string Command, IReadOnlyList<string> Subcommands)> CommandSubcommands =>
        CliCommandMetadata.CommandSubcommands;

    // These command families accept their parent form as the default operation, so completion
    // must offer parent flags alongside nested verbs at the first argument.
    // 親 command 自体が default operation として有効な family。最初の引数では nested verb
    // だけでなく親 command の flag も同時に補完する。
    internal static IReadOnlySet<string> OptionalSubcommandCommands =>
        CliCommandMetadata.OptionalSubcommandCommands;

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
