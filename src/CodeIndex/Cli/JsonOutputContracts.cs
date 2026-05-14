using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

internal sealed record CliJsonMessage(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("message")] string Message);

internal sealed record BackfillFoldJsonResult(
    [property: JsonPropertyName("symbols")] int Symbols,
    [property: JsonPropertyName("symbol_references")] int SymbolReferences,
    [property: JsonPropertyName("rewrite_all")] bool RewriteAll,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("user_version_before")] int UserVersionBefore,
    [property: JsonPropertyName("user_version_after")] int UserVersionAfter,
    [property: JsonPropertyName("fold_ready")] bool FoldReady);

internal sealed record CommandErrorJsonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("hint")] string? Hint);

internal sealed record QueryCountJsonResult(
    [property: JsonPropertyName("count")] int Count);

internal sealed record QueryCountFilesJsonResult(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("query")] string Query);

internal sealed record QueryFindCountJsonResult(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("file_count")] int FileCount);

internal sealed record QueryPathErrorJsonResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("error")] string Error);

internal sealed record LanguageEntryJsonResult(
    [property: JsonPropertyName("lang")] string Lang,
    [property: JsonPropertyName("extensions")] List<string> Extensions,
    [property: JsonPropertyName("aliases")] List<string> Aliases,
    [property: JsonPropertyName("symbol_extraction")] bool SymbolExtraction,
    [property: JsonPropertyName("graph_queries")] bool GraphQueries);

internal sealed record LanguagesJsonResult(
    [property: JsonPropertyName("languages")] List<LanguageEntryJsonResult> Languages);

internal sealed class IndexDryRunJsonResult
{
    public string Status { get; init; } = string.Empty;
    public int FilesTotal { get; init; }
    public Dictionary<string, int> Languages { get; init; } = new();
    public List<CliJsonMessage>? Errors { get; init; }
}

internal sealed class IndexUpdateSummaryJsonResult
{
    public long FilesTotal { get; init; }
    public long ChunksTotal { get; init; }
    public long SymbolsTotal { get; init; }
    public long ReferencesTotal { get; init; }
    public int Updated { get; init; }
    public int Removed { get; init; }
    public int Skipped { get; init; }
    public int Warnings { get; init; }
    public int Errors { get; init; }
}

internal sealed class IndexFullScanSummaryJsonResult
{
    public long FilesTotal { get; init; }
    public long ChunksTotal { get; init; }
    public long SymbolsTotal { get; init; }
    public long ReferencesTotal { get; init; }
    public int FilesScanned { get; init; }
    public int FilesSkipped { get; init; }
    public int FilesPurged { get; init; }
    public int Warnings { get; init; }
    public int Errors { get; init; }
}

internal sealed class IndexUpdateJsonResult
{
    public string Status { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public IndexUpdateSummaryJsonResult Summary { get; init; } = new();
    public bool GraphTableAvailable { get; init; }
    public bool IssuesTableAvailable { get; init; }
    public bool SqlGraphContractReady { get; init; }
    public string? SqlGraphContractDegradedReason { get; init; }
    public bool HotspotFamilyReady { get; init; }
    public string? HotspotFamilyDegradedReason { get; init; }
    [JsonPropertyName("csharp_symbol_name_ready")]
    public bool CSharpSymbolNameReady { get; init; }
    [JsonPropertyName("csharp_metadata_target_ready")]
    public bool CSharpMetadataTargetReady { get; init; }
    public bool FoldReady { get; init; }
    public string? FoldReadyReason { get; init; }
    public string? DegradedReason { get; init; }
    public string? RecommendedAction { get; init; }
    public string? AlternativeAction { get; init; }
    public List<CliJsonMessage>? Errors { get; init; }
    public List<CliJsonMessage>? Warnings { get; init; }
    public long ElapsedMs { get; init; }
}

internal sealed class IndexFullScanJsonResult
{
    public string Status { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public IndexFullScanSummaryJsonResult Summary { get; init; } = new();
    public bool GraphTableAvailable { get; init; }
    public bool IssuesTableAvailable { get; init; }
    public bool SqlGraphContractReady { get; init; }
    public string? SqlGraphContractDegradedReason { get; init; }
    public bool HotspotFamilyReady { get; init; }
    public string? HotspotFamilyDegradedReason { get; init; }
    [JsonPropertyName("csharp_symbol_name_ready")]
    public bool CSharpSymbolNameReady { get; init; }
    [JsonPropertyName("csharp_metadata_target_ready")]
    public bool CSharpMetadataTargetReady { get; init; }
    public bool FoldReady { get; init; }
    public string? FoldReadyReason { get; init; }
    public string? DegradedReason { get; init; }
    public string? RecommendedAction { get; init; }
    public string? AlternativeAction { get; init; }
    public bool HeadChanged { get; init; }
    public string? PriorIndexedHeadCommit { get; init; }
    public string? CurrentHeadCommit { get; init; }
    public string? HeadChangeNotice { get; init; }
    public List<CliJsonMessage>? Errors { get; init; }
    public List<CliJsonMessage>? Warnings { get; init; }
    public long ElapsedMs { get; init; }
}

internal sealed record SymbolHotspotJsonResult(
    string Name,
    string Kind,
    string Path,
    int Line,
    int ReferenceCount,
    string? Visibility,
    string? Container);

internal sealed record GroupedSymbolHotspotJsonResult(
    string Name,
    string Kind,
    string Path,
    int Line,
    int ReferenceCount,
    string? Visibility,
    string? Container,
    int DefinitionSites,
    List<string> Paths);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BackfillFoldJsonResult))]
[JsonSerializable(typeof(CalleeResult))]
[JsonSerializable(typeof(CallerResult))]
[JsonSerializable(typeof(CliJsonMessage))]
[JsonSerializable(typeof(CompactSearchResult))]
[JsonSerializable(typeof(CommandErrorJsonResult))]
[JsonSerializable(typeof(DefinitionResult))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(ExactZeroHintResult))]
[JsonSerializable(typeof(FileDependencyResult))]
[JsonSerializable(typeof(FileExcerptResult))]
[JsonSerializable(typeof(FileFindResult))]
[JsonSerializable(typeof(FileIssue))]
[JsonSerializable(typeof(FileResult))]
[JsonSerializable(typeof(FreshnessHintResult))]
[JsonSerializable(typeof(GroupedHotspotResult))]
[JsonSerializable(typeof(GroupedSymbolHotspotJsonResult))]
[JsonSerializable(typeof(ImpactAnalysisResult))]
[JsonSerializable(typeof(ImpactResult))]
[JsonSerializable(typeof(IndexDryRunJsonResult))]
[JsonSerializable(typeof(IndexFreshnessCheckResult))]
[JsonSerializable(typeof(IndexFullScanJsonResult))]
[JsonSerializable(typeof(IndexFullScanSummaryJsonResult))]
[JsonSerializable(typeof(IndexUpdateJsonResult))]
[JsonSerializable(typeof(IndexUpdateSummaryJsonResult))]
[JsonSerializable(typeof(LanguageEntryJsonResult))]
[JsonSerializable(typeof(LanguagesJsonResult))]
[JsonSerializable(typeof(List<CalleeResult>))]
[JsonSerializable(typeof(List<CallerResult>))]
[JsonSerializable(typeof(List<CliJsonMessage>))]
[JsonSerializable(typeof(List<DefinitionResult>))]
[JsonSerializable(typeof(List<FileDependencyResult>))]
[JsonSerializable(typeof(List<FileIssue>))]
[JsonSerializable(typeof(List<GroupedSymbolHotspotJsonResult>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(List<ImpactResult>))]
[JsonSerializable(typeof(QueryCountJsonResult))]
[JsonSerializable(typeof(QueryCountFilesJsonResult))]
[JsonSerializable(typeof(QueryFindCountJsonResult))]
[JsonSerializable(typeof(QueryPathErrorJsonResult))]
[JsonSerializable(typeof(List<ReferenceResult>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<SymbolHotspotJsonResult>))]
[JsonSerializable(typeof(List<SymbolResult>))]
[JsonSerializable(typeof(List<UnusedSymbolResult>))]
[JsonSerializable(typeof(OutlineResult))]
[JsonSerializable(typeof(OutlineSymbol))]
[JsonSerializable(typeof(QueryCountResult))]
[JsonSerializable(typeof(ReferenceResult))]
[JsonSerializable(typeof(RepoEntrypointResult))]
[JsonSerializable(typeof(RepoFileSummaryResult))]
[JsonSerializable(typeof(RepoLanguageResult))]
[JsonSerializable(typeof(RepoMapResult))]
[JsonSerializable(typeof(RepoModuleResult))]
[JsonSerializable(typeof(SearchHighlight))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(SymbolAnalysisResult))]
[JsonSerializable(typeof(SymbolHotspotJsonResult))]
[JsonSerializable(typeof(SymbolResult))]
[JsonSerializable(typeof(UnusedSymbolResult))]
internal partial class CliJsonSerializerContext : JsonSerializerContext;

internal static class CliJsonSerializerContextFactory
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, CliJsonSerializerContext> s_contexts = new();

    public static CliJsonSerializerContext Create(JsonSerializerOptions jsonOptions) =>
        s_contexts.GetValue(jsonOptions, CreateContext);

    private static CliJsonSerializerContext CreateContext(JsonSerializerOptions jsonOptions)
    {
        var contextOptions = new JsonSerializerOptions(jsonOptions)
        {
            TypeInfoResolver = null,
        };
        return new CliJsonSerializerContext(contextOptions);
    }
}
