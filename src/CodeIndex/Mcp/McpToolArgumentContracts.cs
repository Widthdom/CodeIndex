namespace CodeIndex.Mcp;

public partial class McpServer
{
    private static bool IsKnownToolName(string toolName) => toolName switch
    {
        "search" or "definition" or "references" or "callers" or "callees" or "symbols" or
        "files" or "find_in_file" or "excerpt" or "map" or "analyze_symbol" or "status" or
        "outline" or "batch_query" or "deps" or "impact_analysis" or "languages" or "validate" or
        "unused_symbols" or "symbol_hotspots" or "ping" or "index" or "backfill_fold" or
        "suggest_improvement" => true,
        _ => false,
    };

    private static IReadOnlySet<string> GetAllowedToolArguments(string toolName) => toolName switch
    {
        "search" => new HashSet<string>(StringComparer.Ordinal) { "query", "limit", "lang", "snippetLines", "maxLineWidth", "rawQuery", "cursor", "path", "excludePaths", "excludeTests", "includeGenerated", "since", "noDedup", "exactSubstring", "exact", "prefix", "requireBefore", "requireAfter", "rejectBefore", "rejectAfter", "guardWindow", "countOnly", "format", "project", "solution" },
        "definition" => new HashSet<string>(StringComparer.Ordinal) { "query", "kind", "lang", "limit", "includeBody", "lsp_compatible", "lspCompatible", "path", "excludePaths", "excludeTests", "includeGenerated", "since", "exactName", "exact", "format", "project", "solution" },
        "references" => new HashSet<string>(StringComparer.Ordinal) { "query", "kind", "lang", "limit", "offset", "maxLineWidth", "lsp_compatible", "lspCompatible", "path", "excludePaths", "excludeTests", "includeGenerated", "exactName", "exact", "countOnly", "format", "project", "solution" },
        "callers" or "callees" => new HashSet<string>(StringComparer.Ordinal) { "query", "kind", "rankBy", "lang", "limit", "offset", "path", "excludePaths", "excludeTests", "includeGenerated", "exactName", "exact", "countOnly", "format", "project", "solution" },
        "symbols" => new HashSet<string>(StringComparer.Ordinal) { "query", "names", "kind", "lang", "limit", "path", "excludePaths", "excludeTests", "includeGenerated", "since", "exactName", "exact", "project", "solution" },
        "files" => new HashSet<string>(StringComparer.Ordinal) { "query", "lang", "limit", "path", "excludePaths", "excludeTests", "includeGenerated", "since", "project", "solution" },
        "find_in_file" => new HashSet<string>(StringComparer.Ordinal) { "query", "path", "limit", "lang", "excludePaths", "excludeTests", "includeGenerated", "before", "after", "snippetLines", "focusLine", "focusColumn", "maxLineWidth", "exact", "regex" },
        "excerpt" => new HashSet<string>(StringComparer.Ordinal) { "path", "startLine", "endLine", "before", "after", "focusLine", "focusColumn", "focusLength", "maxLineWidth", "maxOutputBytes" },
        "map" => new HashSet<string>(StringComparer.Ordinal) { "limit", "lang", "path", "excludePaths", "excludeTests", "sections", "depth", "project", "solution" },
        "analyze_symbol" => new HashSet<string>(StringComparer.Ordinal) { "query", "lang", "limit", "includeBody", "path", "excludePaths", "excludeTests", "includeGenerated", "exactName", "exact", "maxLineWidth", "project", "solution" },
        "outline" => new HashSet<string>(StringComparer.Ordinal) { "path" },
        "batch_query" => new HashSet<string>(StringComparer.Ordinal) { "queries", "maxResponseBytes", "estimateOnly" },
        "deps" => new HashSet<string>(StringComparer.Ordinal) { "path", "reverse", "format", "cycles", "lang", "limit", "excludePaths", "excludeTests", "project", "solution" },
        "impact_analysis" => new HashSet<string>(StringComparer.Ordinal) { "query", "lang", "maxHops", "maxDepth", "limit", "path", "excludePaths", "excludeTests", "includeGenerated", "withPaths", "countOnly", "project", "solution" },
        "validate" => new HashSet<string>(StringComparer.Ordinal) { "kind", "path", "excludePaths", "excludeTests", "project", "solution" },
        "unused_symbols" => new HashSet<string>(StringComparer.Ordinal) { "kind", "lang", "limit", "path", "excludePaths", "excludeTests", "bucket", "minConfidence", "project", "solution" },
        "symbol_hotspots" => new HashSet<string>(StringComparer.Ordinal) { "kind", "lang", "limit", "groupBy", "path", "excludePaths", "excludeTests", "project", "solution" },
        "index" => new HashSet<string>(StringComparer.Ordinal) { "path", "rebuild", "maxFileBytes" },
        "backfill_fold" => new HashSet<string>(StringComparer.Ordinal) { "dry_run", "dryRun", "force" },
        "suggest_improvement" => new HashSet<string>(StringComparer.Ordinal) { "category", "language", "description", "context", "toolInvocationContext", "evidencePaths", "evidence_paths" },
        _ => new HashSet<string>(StringComparer.Ordinal),
    };
}
