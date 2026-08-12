using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode? DispatchSynchronousToolCall(string toolName, JsonNode? id, JsonNode? args)
        => toolName switch
        {
            "search" => ExecuteSearch(id, args),
            "definition" => ExecuteDefinition(id, args),
            "references" => ExecuteReferences(id, args),
            "callers" => ExecuteCallers(id, args),
            "callees" => ExecuteCallees(id, args),
            "symbols" => ExecuteSymbols(id, args),
            "files" => ExecuteFiles(id, args),
            "find_in_file" => ExecuteFindInFile(id, args),
            "excerpt" => ExecuteExcerpt(id, args),
            "read_resource" => ExecuteReadResource(id, args),
            "map" => ExecuteMap(id, args),
            "analyze_symbol" => ExecuteAnalyzeSymbol(id, args),
            "status" => ExecuteStatus(id, args),
            "outline" => ExecuteOutline(id, args),
            "deps" => ExecuteDeps(id, args),
            "impact_analysis" => ExecuteImpactAnalysis(id, args),
            "languages" => ExecuteLanguages(id, args),
            "validate" => ExecuteValidate(id, args),
            "unused_symbols" => ExecuteUnusedSymbols(id, args),
            "symbol_hotspots" => ExecuteSymbolHotspots(id, args),
            "ping" => ExecutePing(id),
            _ => null,
        };
}
