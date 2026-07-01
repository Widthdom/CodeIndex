using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed record McpIndexRequestOptions(
        string Path,
        bool Rebuild,
        bool DryRun,
        bool MemoryTrace,
        int? RequestedParallelism,
        int? RequestedDebounce,
        long? MaxFileBytes,
        int MaxSymbolsPerFile,
        int MaxReferencesPerFile,
        FileIndexer.SymlinkPolicy SymlinkPolicy,
        IReadOnlyList<string> IncludeSymbolKinds,
        IReadOnlyList<string> ExcludeSymbolKinds,
        SymbolKindFilter SymbolKindFilter,
        IReadOnlyList<McpIndexUnsupportedMode> UnsupportedModes,
        JsonObject OptionsPayload);

    private bool TryReadMcpIndexRequestOptions(
        JsonNode? id,
        JsonNode? args,
        out McpIndexRequestOptions? options,
        out JsonObject? errorResponse)
    {
        options = null;
        errorResponse = null;

        if (!TryReadRequiredIndexPathParameter(args, "path", out var path, out var requiredError))
        {
            errorResponse = CreateToolErrorResponse(id, requiredError!);
            return false;
        }

        var rebuild = args?["rebuild"]?.GetValue<bool>() ?? false;
        var dryRun = args?["dryRun"]?.GetValue<bool>() ?? args?["dry_run"]?.GetValue<bool>() ?? false;
        var memoryTrace = args?["memoryTrace"]?.GetValue<bool>() ?? false;
        var requestedParallelism = ReadOptionalIntArgument(args, "parallelism");
        var requestedDebounce = ReadOptionalIntArgument(args, "debounce");

        var maxSymbolsPerFile = ReadOptionalIntArgument(args, "maxSymbolsPerFile") ?? IndexCommandRunner.DefaultMaxSymbolsPerFile;
        if (maxSymbolsPerFile <= 0 || maxSymbolsPerFile > IndexCommandRunner.MaxSymbolsPerFileLimit)
        {
            errorResponse = CreateToolErrorResponse(id, $"maxSymbolsPerFile must be between 1 and {IndexCommandRunner.MaxSymbolsPerFileLimit}");
            return false;
        }

        var maxReferencesPerFile = ReadOptionalIntArgument(args, "maxReferencesPerFile") ?? IndexCommandRunner.DefaultMaxReferencesPerFile;
        if (maxReferencesPerFile <= 0 || maxReferencesPerFile > IndexCommandRunner.MaxReferencesPerFileLimit)
        {
            errorResponse = CreateToolErrorResponse(id, $"maxReferencesPerFile must be between 1 and {IndexCommandRunner.MaxReferencesPerFileLimit}");
            return false;
        }

        if (!TryReadMcpIndexSymlinkPolicy(args, out var symlinkPolicy, out var symlinkPolicyError))
        {
            errorResponse = CreateToolErrorResponse(id, symlinkPolicyError!);
            return false;
        }

        var includeSymbolKinds = ReadStringOrCommaSeparatedList(args, "includeSymbolKind");
        var excludeSymbolKinds = ReadStringOrCommaSeparatedList(args, "excludeSymbolKind");
        var symbolKindFilter = SymbolKindFilter.Create(includeSymbolKinds, excludeSymbolKinds, parseError: null);
        if (symbolKindFilter.ParseError != null)
        {
            errorResponse = CreateToolErrorResponse(id, symbolKindFilter.ParseError);
            return false;
        }

        var unsupportedModes = BuildMcpIndexUnsupportedModes(args, requestedParallelism, requestedDebounce);
        if (!TryReadMcpIndexMaxFileBytes(id, args, out var maxFileBytes, out errorResponse))
            return false;

        var optionsPayload = BuildMcpIndexOptionsPayload(
            dryRun,
            rebuild,
            maxFileBytes,
            maxSymbolsPerFile,
            maxReferencesPerFile,
            symlinkPolicy,
            includeSymbolKinds,
            excludeSymbolKinds,
            memoryTrace,
            requestedParallelism,
            requestedDebounce,
            args);

        options = new McpIndexRequestOptions(
            path!,
            rebuild,
            dryRun,
            memoryTrace,
            requestedParallelism,
            requestedDebounce,
            maxFileBytes,
            maxSymbolsPerFile,
            maxReferencesPerFile,
            symlinkPolicy,
            includeSymbolKinds,
            excludeSymbolKinds,
            symbolKindFilter,
            unsupportedModes,
            optionsPayload);
        return true;
    }

    private bool TryReadMcpIndexMaxFileBytes(
        JsonNode? id,
        JsonNode? args,
        out long? maxFileBytes,
        out JsonObject? errorResponse)
    {
        maxFileBytes = null;
        errorResponse = null;

        if (args?["maxFileBytes"] is { } maxFileBytesNode)
        {
            try
            {
                maxFileBytes = maxFileBytesNode.GetValue<long>();
            }
            catch (Exception)
            {
                errorResponse = CreateToolErrorResponse(id, "maxFileBytes must be a positive integer less than or equal to 2147483647");
                return false;
            }
        }

        if (maxFileBytes is <= 0 or > int.MaxValue)
        {
            errorResponse = CreateToolErrorResponse(id, "maxFileBytes must be a positive integer less than or equal to 2147483647");
            return false;
        }

        return true;
    }
}
