using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace CodeIndex.Cli;

/// <summary>
/// Dependency-neutral, immutable command metadata shared by command routing, help, and
/// shell completion generation. Keep behavior and rendering dependencies out of this type.
/// command routing、help、shell completion が共有する、依存関係に中立で不変な metadata。
/// この型には挙動や rendering への依存を追加しない。
/// </summary>
internal static class CliCommandMetadata
{
    internal static IReadOnlyList<string> PublicCommandNames { get; } = Array.AsReadOnly(
    [
        "index", "hooks", "backfill-fold", "optimize", "vacuum", "search", "recipes", "audit", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status", "workspace", "config", "upgrade", "validate-config",
        "doctor", "db", "diff", "report", "validate", "deps", "impact", "unused", "hotspots", "suggestions", "export", "import", "languages", "batch", "mcp", "lsp", "completions", "license", "help",
    ]);

    internal static IReadOnlyDictionary<string, string> CommandAliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["refs"] = "references",
            ["stats"] = "status",
            ["fold"] = "backfill-fold",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static IReadOnlyList<(string Command, IReadOnlyList<string> Subcommands)> CommandSubcommands { get; } =
        Array.AsReadOnly<(string Command, IReadOnlyList<string> Subcommands)>(
        [
            ("hooks", ReadOnly("install", "uninstall", "status")),
            ("workspace", ReadOnly("list", "status", "use", "current", "clear", "deactivate")),
            ("config", ReadOnly("show")),
            ("db", ReadOnly("integrity", "schema", "prune", "checkpoint", "checkpoints", "restore", "restore-backups")),
            ("recipes", ReadOnly("list")),
            ("suggestions", ReadOnly("list", "show", "export", "add", "update", "delete")),
            ("export", ReadOnly("ctags")),
        ]);

    internal static IReadOnlySet<string> OptionalSubcommandCommands { get; } =
        new[] { "recipes", "suggestions" }.ToFrozenSet(StringComparer.Ordinal);

    // These commands render process-static metadata and must not discover or parse
    // project configuration. validate-config owns malformed-config reporting so it
    // can preserve its command-specific contract.
    // これらの command は process-static metadata を描画するため、project config を
    // 探索・parse しない。validate-config は command 固有契約を保つため、不正な config の
    // reporting を自身で所有する。
    internal static IReadOnlySet<string> ProjectConfigIndependentCommands { get; } =
        new[] { "help", "completions", "license" }.ToFrozenSet(StringComparer.Ordinal);

    internal static IReadOnlySet<string> ProjectConfigSelfManagedCommands { get; } =
        new[] { "validate-config" }.ToFrozenSet(StringComparer.Ordinal);

    private static ReadOnlyCollection<string> ReadOnly(params string[] values) =>
        Array.AsReadOnly(values);
}
