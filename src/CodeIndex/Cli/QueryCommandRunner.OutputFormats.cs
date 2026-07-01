using System.Text.Json.Nodes;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const string JsonOutputFormatNdjson = "ndjson";
    private const string JsonOutputFormatArray = "array";
    private const string OutputFormatText = "text";
    private const string OutputFormatJson = "json";
    private const string OutputFormatLsp = "lsp";
    private const string OutputFormatQf = "qf";
    private const string OutputFormatSarif = "sarif";
    private const string OutputFormatCount = "count";
    private const string OutputFormatCompact = "compact";
    private const string OutputFormatGrouped = "grouped";
    private const string OutputFormatCsv = "csv";
    private const string OutputFormatTsv = "tsv";
    private const string OutputFormatIssueDrafts = "issue-drafts";
    private const string OutputFormatDot = "dot";
    private const string OutputFormatGraphMl = "graphml";
    private const string OutputFormatJsonGraph = "json-graph";
    private const string OutputFormatEdgeList = "edgelist";

    private static readonly HashSet<string> RepoMapOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCompact,
        OutputFormatIssueDrafts,
    };

    private static readonly HashSet<string> SymbolOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCount,
        OutputFormatCompact,
        OutputFormatLsp,
        OutputFormatQf,
        OutputFormatSarif,
    };

    private static readonly HashSet<string> FilesOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCount,
        OutputFormatCompact,
    };

    private static readonly HashSet<string> InspectOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCompact,
    };

    private static void AddJsonByteLimitField(JsonObject payload, QueryCommandOptions options)
    {
        if (options.MaxJsonBytes.HasValue)
            payload["output_byte_limit"] = options.MaxJsonBytes.Value;
    }
}
