namespace CodeIndex.Cli;

internal static class CliCommandCatalog
{
    internal static readonly string[] Commands =
    [
        "index", "hooks", "backfill-fold", "optimize", "vacuum", "search", "recipes", "audit", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status", "workspace", "config", "upgrade", "validate-config",
        "doctor", "db", "diff", "report", "validate", "deps", "impact", "unused", "hotspots", "suggestions", "export", "import", "languages", "batch", "mcp", "lsp", "completions", "license",
    ];
}
