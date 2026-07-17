namespace CodeIndex.Cli;

internal sealed record CliOutputFormatCapability(
    string Name,
    bool IsJsonContract,
    bool SupportsPretty,
    bool SupportsJsonStreamMode);

internal static class CliOutputFormatCapabilities
{
    private static readonly CliOutputFormatCapability[] Ordered =
    [
        new("text", false, false, false),
        new("json", true, true, true),
        new("count", true, true, false),
        new("compact", true, true, false),
        new("grouped", true, true, false),
        new("csv", false, false, false),
        new("tsv", false, false, false),
        new("lsp", true, true, false),
        new("qf", false, false, false),
        new("sarif", true, true, false),
        new("dot", false, false, false),
        new("graphml", false, false, false),
        new("json-graph", true, true, false),
        new("edgelist", false, false, false),
        new("markdown", false, false, false),
        new("issue-drafts", true, true, false),
    ];

    private static readonly IReadOnlyDictionary<string, CliOutputFormatCapability> ByName =
        Ordered.ToDictionary(capability => capability.Name, StringComparer.OrdinalIgnoreCase);

    internal static string FormatValuePlaceholder { get; } =
        $"<{string.Join('|', Ordered.Select(capability => capability.Name))}>";

    internal static string FormatDescription { get; } =
        $"Standard output format for token budgets, editor integrations, and CI; JSON contracts: {string.Join(", ", Ordered.Where(capability => capability.IsJsonContract).Select(capability => capability.Name))}; supported values vary by command";

    internal static string PrettyDescription { get; } =
        "Pretty-print single-document JSON output with indentation; incompatible with NDJSON";

    internal static bool TryGet(string format, out CliOutputFormatCapability capability) =>
        ByName.TryGetValue(format, out capability!);
}
