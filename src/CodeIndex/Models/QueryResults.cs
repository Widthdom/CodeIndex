using System.Globalization;
using System.Text.Json.Serialization;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Database;

// Result DTOs for query operations / クエリ操作用の結果DTO
// Extracted from DbReader.cs for file-size reduction.
// ファイルサイズ削減のため DbReader.cs から分離。

public class SearchResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
    public string? Visibility { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnclosingSymbolName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnclosingSymbolKind { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EnclosingSymbolStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EnclosingSymbolEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnclosingContainerName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnclosingSymbolReturnType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SearchGuardEvidence>? GuardEvidence { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SearchGuardCheck>? GuardChecks { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SearchDiagnostic>? Diagnostics { get; set; }
    [JsonIgnore]
    public long ChunkId { get; set; }
    [JsonIgnore]
    public int NextOffset { get; set; }
}

public sealed class SearchDiagnostic
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Limit { get; set; }
}

public readonly record struct SearchCursor(double Score, long ChunkId, int Offset);

public readonly record struct QueryCountResult(
    int Count,
    int FileCount,
    bool IncludesSql = false,
    long? TotalBytes = null,
    double? AverageBytes = null,
    long? MaxBytes = null,
    string? MaxBytesPath = null,
    bool? BytesAuthoritative = null);

public readonly record struct UnusedCountResult(
    int Count,
    int FileCount,
    bool IncludesSql,
    IReadOnlyDictionary<string, int> BucketCounts,
    IReadOnlyDictionary<string, int> ConfidenceCounts,
    IReadOnlyDictionary<string, int> ContractDomainCounts);

public readonly record struct SearchFileCountResult(string Path, int Count);

public readonly record struct FindScanSummary(
    int CandidateFiles,
    int FilesScanned,
    int LinesScanned,
    bool Truncated = false,
    bool CapReached = false,
    bool TimedOut = false,
    string? TruncationReason = null,
    int? CandidateFileLimit = null,
    int? LineLimit = null,
    string SearchStrategy = "line_scan",
    string? SearchFallbackReason = null,
    string? NextPath = null,
    int? NextLine = null,
    int? NextFileOrdinal = null,
    int? NextMatchOrdinal = null,
    int? NextByteOffset = null,
    bool ResultLimitReached = false);

public readonly record struct FindCountResult(int Count, int FileCount, FindScanSummary Scan);

public readonly record struct FindResults(List<FileFindResult> Results, FindScanSummary Scan) : IReadOnlyList<FileFindResult>
{
    public int Count => Results.Count;

    public FileFindResult this[int index] => Results[index];

    public List<FileFindResult>.Enumerator GetEnumerator() => Results.GetEnumerator();

    IEnumerator<FileFindResult> IEnumerable<FileFindResult>.GetEnumerator() => Results.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Results.GetEnumerator();
}

public readonly record struct HotspotCountResult(int Count, int FileCount, int DefinitionSiteTotal = 0);

public enum SearchGuardRole
{
    Require,
    Reject,
}

public enum SearchGuardDirection
{
    Before,
    After,
}

public enum SearchGuardScope
{
    Window,
    SameLine,
    Container,
}

public enum SearchGuardEvidenceKind
{
    Text,
    CSharpBoundedFileRead,
    CSharpEnumerationOptions,
}

public enum SearchResultRanking
{
    Default,
    CredentialContext,
}

public sealed record SearchGuardFilter(
    SearchGuardRole Role,
    SearchGuardDirection Direction,
    string Query,
    SearchGuardScope? Scope = null,
    SearchGuardEvidenceKind EvidenceKind = SearchGuardEvidenceKind.Text);

public sealed class SearchGuardEvidence
{
    public string Role { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string Relationship { get; set; } = string.Empty;
    public string Decision { get; set; } = "accepted";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidencePath { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Container { get; set; }
    public SearchGuardSpan Span { get; set; } = new();
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
    public string Origin { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class SearchGuardCheck
{
    public string Role { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string Relationship { get; set; } = string.Empty;
    public bool Matched { get; set; }
    public bool Passed { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int WindowStartLine { get; set; }
    public int WindowEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchGuardEvidence? Evidence { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SearchGuardEvidence>? RejectedEvidence { get; set; }
}

public sealed class SearchGuardSpan
{
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
}

public sealed class SearchMatchFacet
{
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
    public string Origin { get; set; } = string.Empty;
    public bool TestFile { get; set; }
    public bool TestSymbol { get; set; }
    public bool TestFixture { get; set; }
}

public sealed record FtsQueryDiagnostics(
    [property: JsonPropertyName("query_degraded_reason")] string? QueryDegradedReason,
    [property: JsonPropertyName("tokens_dropped")] IReadOnlyList<string> TokensDropped)
{
    public static FtsQueryDiagnostics None { get; } = new(null, []);
    public bool HasDegradation => QueryDegradedReason != null;
}

public enum ReferenceRankMode
{
    Weighted,
    Count,
    Kind,
}

internal enum ReferenceRankDimension
{
    ReferenceWeightScoreDescending,
    ReferenceCountDescending,
    ReferenceKindPriorityAscending,
    ExactCaseMatchDescending,
    ExactNameMatchDescending,
    PathCategoryAscending,
    PathAscending,
    FirstLineAscending,
    FirstColumnAscending,
    LanguageAscending,
    ContainerKindAscending,
    ContainerNameAscending,
    SymbolNameAscending,
    ReferenceKindAscending,
}

internal static class ReferenceRankRecipes
{
    private static readonly IReadOnlyList<ReferenceRankDimension> StableTieBreakers =
        Array.AsReadOnly(
        [
            ReferenceRankDimension.ExactCaseMatchDescending,
            ReferenceRankDimension.ExactNameMatchDescending,
            ReferenceRankDimension.PathCategoryAscending,
            ReferenceRankDimension.PathAscending,
            ReferenceRankDimension.FirstLineAscending,
            ReferenceRankDimension.FirstColumnAscending,
            ReferenceRankDimension.LanguageAscending,
            ReferenceRankDimension.ContainerKindAscending,
            ReferenceRankDimension.ContainerNameAscending,
            ReferenceRankDimension.SymbolNameAscending,
            ReferenceRankDimension.ReferenceKindAscending,
        ]);

    private static readonly IReadOnlyList<ReferenceRankDimension> Weighted =
        Build(
            ReferenceRankDimension.ReferenceWeightScoreDescending,
            ReferenceRankDimension.ReferenceCountDescending);

    private static readonly IReadOnlyList<ReferenceRankDimension> Count =
        Build(ReferenceRankDimension.ReferenceCountDescending);

    private static readonly IReadOnlyList<ReferenceRankDimension> Kind =
        Build(
            ReferenceRankDimension.ReferenceKindPriorityAscending,
            ReferenceRankDimension.ReferenceCountDescending);

    internal static IReadOnlyList<ReferenceRankDimension> Get(ReferenceRankMode mode) => mode switch
    {
        ReferenceRankMode.Count => Count,
        ReferenceRankMode.Kind => Kind,
        _ => Weighted,
    };

    internal static string Format(ReferenceRankDimension dimension) => dimension switch
    {
        ReferenceRankDimension.ReferenceWeightScoreDescending => "reference_weight_score_desc",
        ReferenceRankDimension.ReferenceCountDescending => "reference_count_desc",
        ReferenceRankDimension.ReferenceKindPriorityAscending => "reference_kind_priority_asc",
        ReferenceRankDimension.ExactCaseMatchDescending => "exact_case_match_desc",
        ReferenceRankDimension.ExactNameMatchDescending => "exact_name_match_desc",
        ReferenceRankDimension.PathCategoryAscending => "path_category_asc",
        ReferenceRankDimension.PathAscending => "path_asc",
        ReferenceRankDimension.FirstLineAscending => "first_line_asc",
        ReferenceRankDimension.FirstColumnAscending => "first_column_asc",
        ReferenceRankDimension.LanguageAscending => "language_asc",
        ReferenceRankDimension.ContainerKindAscending => "container_kind_asc",
        ReferenceRankDimension.ContainerNameAscending => "container_name_asc",
        ReferenceRankDimension.SymbolNameAscending => "symbol_name_asc",
        ReferenceRankDimension.ReferenceKindAscending => "reference_kind_asc",
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null),
    };

    private static IReadOnlyList<ReferenceRankDimension> Build(
        params ReferenceRankDimension[] primaryDimensions)
    {
        var recipe = new ReferenceRankDimension[
            primaryDimensions.Length + StableTieBreakers.Count];
        primaryDimensions.CopyTo(recipe, 0);
        for (var index = 0; index < StableTieBreakers.Count; index++)
            recipe[primaryDimensions.Length + index] = StableTieBreakers[index];
        return Array.AsReadOnly(recipe);
    }
}

public enum SymbolSortMode
{
    Name,
    Hotspot,
    References,
    Size,
    Complexity,
    Path,
}

public enum OutlineSortMode
{
    Source,
    Name,
    Kind,
    References,
    Size,
    Complexity,
    Path,
}

public class SymbolResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    [JsonPropertyName("symbol_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SymbolId { get; set; }
    public string? Lang { get; set; }
    public string Kind { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubKind { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Line { get; set; }
    public int StartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartColumn { get; set; }
    public int EndLine { get; set; }
    public int? BodyStartLine { get; set; }
    public int? BodyEndLine { get; set; }
    public string? Signature { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SignatureTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SignatureOriginalLength { get; set; }
    public string? ContainerKind { get; set; }
    public string? ContainerName { get; set; }
    [JsonIgnore]
    public string? ContainerQualifiedName { get; set; }
    internal string? LogicalPartialKey { get; set; }
    public string? Visibility { get; set; }
    public string? ReturnType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SortMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HotspotScore { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RankingReferenceScore { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RankingHotspotScore { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? GenericNamePenalty { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StructuralRankPenalty { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DefinitionSites { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialFamilyId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepresentativeReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PartialFamilyMember>? FamilyMembers { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FamilyMembersTruncated { get; set; }
    [JsonIgnore]
    internal bool? IsGeneratedCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SizeLines { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ComplexityScore { get; set; }
}

public sealed class PartialFamilyMember
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SymbolId { get; set; }
    public string Path { get; set; } = string.Empty;
    public int Line { get; set; }
    public int StartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartColumn { get; set; }
    public int EndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Generated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Representative { get; set; }
}

public class UnusedSymbolResult : SymbolResult
{
    public string UnusedBucket { get; set; } = string.Empty;
    public string UnusedConfidence { get; set; } = string.Empty;
    public string UnusedReason { get; set; } = string.Empty;
    public List<string> UnusedReasonTags { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnusedContractDomain { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? UnusedContractDomainTags { get; set; }
}

public class GroupedHotspotResult
{
    public SymbolResult Symbol { get; set; } = new();
    public int ReferenceCount { get; set; }
    public double ReferenceScore { get; set; }
    public double RankingScore { get; set; }
    public double GenericNamePenalty { get; set; } = 1.0;
    public int DefinitionSites { get; set; }
    public List<string> Paths { get; set; } = [];
    public bool PathsTruncated { get; set; }
    public List<GroupedHotspotDefinitionSite> DefinitionSiteDetails { get; set; } = [];
}

public class GroupedHotspotDefinitionSite
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int Line { get; set; }
    public string? Visibility { get; set; }
    public string? Container { get; set; }
    public string? LogicalTargetKey { get; set; }
}

public class SymbolHotspotResult
{
    public SymbolResult Symbol { get; set; } = new();
    public int ReferenceCount { get; set; }
    public double ReferenceScore { get; set; }
    public double RankingScore { get; set; }
    public double GenericNamePenalty { get; set; } = 1.0;
}

public class FileHotspotResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int ReferenceCount { get; set; }
    public double ReferenceScore { get; set; }
    public double RankingScore { get; set; }
    public double GenericNamePenalty { get; set; } = 1.0;
    public double StructuralRankPenalty { get; set; } = 1.0;
    public int SymbolCount { get; set; }
}

public class FileResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public long Size { get; set; }
    public int Lines { get; set; }
    public int SymbolCount { get; set; }
    public int ReferenceCount { get; set; }
    public string? Checksum { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? IndexedAt { get; set; }
}

public class FileExcerptResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int RequestedStartLine { get; set; }
    public int RequestedEndLine { get; set; }
    public int EffectiveStartLine { get; set; }
    public int EffectiveEndLine { get; set; }
    public int TotalLines { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool ContentTruncated { get; set; }
    public List<string> ContentTruncationReasons { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? ContentRecovery { get; set; }
    public string SemanticTokenCoordinateSpace { get; set; } = "source";
    public List<ExcerptContentLineSpan> ContentLineSpans { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExcerptSemanticToken>? SemanticTokens { get; set; }

    public static ExcerptRecoveryHint CreateRecoveryHint(string path, int startLine, int endLine)
        => new()
        {
            StartLine = startLine,
            EndLine = endLine,
            Argv = [
                "cdidx",
                "excerpt",
                path,
                "--start",
                startLine.ToString(CultureInfo.InvariantCulture),
                "--end",
                endLine.ToString(CultureInfo.InvariantCulture),
                "--max-line-width",
                "0",
                "--json",
            ],
        };
}

/// <summary>
/// A byte-bounded page reconstructed directly from indexed chunk blobs.
/// インデックス済みチャンクBLOBから直接再構成したbyte上限付きページ。
/// </summary>
public enum BoundedFileReadStatus
{
    Success,
    Empty,
    FileNotFound,
    InvalidContinuation,
    IncompleteCoverage,
    ContentUnavailable,
    InvalidTopology,
}

public sealed class BoundedFileReadResult
{
    public BoundedFileReadStatus Status { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int TotalLines { get; set; }
    public int RequestedStartLine { get; set; }
    public int RequestedEndLine { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = string.Empty;
    public int Utf8Bytes { get; set; }
    public bool Truncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TruncationReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NextLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NextByteOffset { get; set; }
}

public class ExcerptContentLineSpan
{
    public int ContentLine { get; set; }
    public int SourceLine { get; set; }
    public int ContentStartColumn { get; set; }
    public int ContentEndColumn { get; set; }
    public int SourceStartColumn { get; set; }
    public int SourceEndColumn { get; set; }
}

public class ExcerptSemanticToken
{
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public string Type { get; set; } = string.Empty;
    public List<string> Modifiers { get; set; } = [];
}

public class ExcerptRecoveryHint
{
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public List<string> Argv { get; set; } = [];
    public string Command { get; set; } = string.Empty;
    public string CommandShell { get; set; } = string.Empty;
    public bool CommandDisplayOnly { get; set; } = true;
    public bool PathsRedacted { get; set; } = true;
    public bool RequiresLocalPathSubstitution { get; set; }
}

public class FileFindResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
    public int OriginalLineLength { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Snippet { get; set; } = string.Empty;
    public bool SnippetTruncated { get; set; }
    public FileFindSnippetTruncationContext SnippetTruncationContext { get; set; } = new();
}

public class FileFindSnippetTruncationContext
{
    public int LineCount { get; set; }
    public List<int> CharCounts { get; set; } = [];
    public int TotalChars { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}

public class IndexFreshnessCheckResult
{
    public bool Checked { get; set; }
    [JsonPropertyName("matches_workspace")]
    public bool MatchesWorkspace { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int IndexedFileCount { get; set; }
    public int WorkspaceFileCount { get; set; }
    public int MatchedFileCount { get; set; }
    public int ChangedFileCount { get; set; }
    public int MissingFileCount { get; set; }
    public int OutsideSparseConeFileCount { get; set; }
    public int UnindexedFileCount { get; set; }
    public int UnverifiableFileCount { get; set; }
    public int ScanErrorCount { get; set; }
    public List<string> ChangedFiles { get; set; } = [];
    public List<string> MissingFiles { get; set; } = [];
    public List<string> OutsideSparseConeFiles { get; set; } = [];
    public List<string> UnindexedFiles { get; set; } = [];
    public List<string> UnverifiableFiles { get; set; } = [];
    public List<string> ScanErrors { get; set; } = [];
    public bool ChangedFilesTruncated => ChangedFilesOmittedCount > 0;
    public int ChangedFilesPathLimit => WorkspaceCheckPathSamples.PathLimit;
    public int ChangedFilesOmittedCount => WorkspaceCheckPathSamples.GetOmittedCount(ChangedFileCount, ChangedFiles);
    public bool MissingFilesTruncated => MissingFilesOmittedCount > 0;
    public int MissingFilesPathLimit => WorkspaceCheckPathSamples.PathLimit;
    public int MissingFilesOmittedCount => WorkspaceCheckPathSamples.GetOmittedCount(MissingFileCount, MissingFiles);
    public bool OutsideSparseConeFilesTruncated => OutsideSparseConeFilesOmittedCount > 0;
    public int OutsideSparseConeFilesPathLimit => WorkspaceCheckPathSamples.PathLimit;
    public int OutsideSparseConeFilesOmittedCount => WorkspaceCheckPathSamples.GetOmittedCount(OutsideSparseConeFileCount, OutsideSparseConeFiles);
    public bool UnindexedFilesTruncated => UnindexedFilesOmittedCount > 0;
    public int UnindexedFilesPathLimit => WorkspaceCheckPathSamples.PathLimit;
    public int UnindexedFilesOmittedCount => WorkspaceCheckPathSamples.GetOmittedCount(UnindexedFileCount, UnindexedFiles);
    public bool UnverifiableFilesTruncated => UnverifiableFilesOmittedCount > 0;
    public int UnverifiableFilesPathLimit => WorkspaceCheckPathSamples.PathLimit;
    public int UnverifiableFilesOmittedCount => WorkspaceCheckPathSamples.GetOmittedCount(UnverifiableFileCount, UnverifiableFiles);
    public bool ScanErrorsTruncated => ScanErrorsOmittedCount > 0;
    public int ScanErrorsPathLimit => WorkspaceCheckPathSamples.PathLimit;
    public int ScanErrorsOmittedCount => WorkspaceCheckPathSamples.GetOmittedCount(ScanErrorCount, ScanErrors);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadCommit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceHeadCommit { get; set; }
    public bool HeadChanged { get; set; }
}

internal static class WorkspaceCheckPathSamples
{
    internal const int PathLimit = 20;

    internal static IReadOnlyList<WorkspaceCheckPathSampleDescriptor> Descriptors { get; } =
    [
        new("changed_file_count", "changed_files", "changed_files_truncated", "changed_files_path_limit", "changed_files_omitted_count"),
        new("missing_file_count", "missing_files", "missing_files_truncated", "missing_files_path_limit", "missing_files_omitted_count"),
        new("outside_sparse_cone_file_count", "outside_sparse_cone_files", "outside_sparse_cone_files_truncated", "outside_sparse_cone_files_path_limit", "outside_sparse_cone_files_omitted_count"),
        new("unindexed_file_count", "unindexed_files", "unindexed_files_truncated", "unindexed_files_path_limit", "unindexed_files_omitted_count"),
        new("unverifiable_file_count", "unverifiable_files", "unverifiable_files_truncated", "unverifiable_files_path_limit", "unverifiable_files_omitted_count"),
        new("scan_error_count", "scan_errors", "scan_errors_truncated", "scan_errors_path_limit", "scan_errors_omitted_count"),
    ];

    internal static int GetOmittedCount(int authoritativeCount, IReadOnlyCollection<string> samples)
        => Math.Max(0, authoritativeCount - samples.Count);
}

internal readonly record struct WorkspaceCheckPathSampleDescriptor(
    string CountPropertyName,
    string ListPropertyName,
    string TruncatedPropertyName,
    string PathLimitPropertyName,
    string OmittedCountPropertyName);

public class DefinitionResult : SymbolResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LspRange? Range { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Disambiguator { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? BodyContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyContentStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyContentEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyContentNextStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BodyContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BodyContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? BodyContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Complexity { get; set; }
}

public sealed class LspPosition
{
    public int Line { get; set; }
    public int Character { get; set; }
}

public sealed class LspRange
{
    public LspPosition Start { get; set; } = new();
    public LspPosition End { get; set; } = new();
}

public sealed class LspLocation
{
    public string Uri { get; set; } = string.Empty;
    public LspRange Range { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SymbolId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialFamilyId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepresentativeReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LspLocation>? FamilyMembers { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FamilyMembersTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Representative { get; set; }
}

public class ExactZeroHintResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RelaxedCount { get; set; }
    public const string DefaultSuggestion = "drop --exact or use the exact indexed name";
    public List<string> SampleNames { get; set; } = [];
    public string Suggestion { get; set; } = DefaultSuggestion;

    public static ExactZeroHintResult? FromRelaxedMatches(int relaxedCount, IEnumerable<string?> names, int sampleLimit = 5)
    {
        if (relaxedCount <= 0)
            return null;

        var sampleNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(sampleLimit)
            .Select(name => name!)
            .ToList();

        return new ExactZeroHintResult
        {
            RelaxedCount = relaxedCount,
            SampleNames = sampleNames,
            Suggestion = DefaultSuggestion,
        };
    }
}

public class FreshnessHintResult
{
    public long FileCount { get; set; }
    public DateTime? IndexedAt { get; set; }
    public bool FreshnessAvailable { get; set; }
    public string? FreshnessDegradedReason { get; set; }
}

public class ReferenceResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string SymbolName { get; set; } = string.Empty;
    public string ReferenceKind { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    [JsonIgnore]
    public int? SpanLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LspRange? Range { get; set; }
    [JsonIgnore]
    public string RawContext { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public bool ContextTruncated { get; set; }
    public string? ContainerKind { get; set; }
    public string? ContainerName { get; set; }
    public bool IsSelfReference { get; set; }
    public bool IsMutualRecursion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TargetSymbolId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetSymbolKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolutionState { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ResolutionCandidateCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BodyContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BodyContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BodyContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? BodyContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CallsiteContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CallsiteContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? CallsiteContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteColumn { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteSelection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteOmittedReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteContentUnavailableReason { get; set; }
}

public class CallerResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? CallerKind { get; set; }
    public string? CallerName { get; set; }
    public string CalleeName { get; set; } = string.Empty;
    [JsonIgnore]
    public long? CallerSymbolId { get; set; }
    [JsonIgnore]
    public long? CalleeSymbolId { get; set; }
    // Canonical cycle analysis keeps every resolved target behind an aggregated caller row;
    // CalleeSymbolId remains the output-safe scalar and is null when those targets differ.
    // 集約 caller 行の全 resolved target は cycle 判定用に保持し、target が異なる場合は
    // output 用 scalar の CalleeSymbolId を null にする。
    [JsonIgnore]
    public IReadOnlyList<long> CalleeSymbolIds { get; set; } = Array.Empty<long>();
    // Summary preferred reference_kind for the grouped row. Grouped caller rows can
    // collapse multiple underlying kinds into one label, so JSON/MCP consumers that
    // need the full picture should read ReferenceKinds + HasMixedReferenceKinds as
    // well (issue #501). The scalar is kept for back-compat with existing consumers.
    // グループ化された行は複数の reference_kind を 1 ラベルに畳むため、
    // JSON/MCP で全体を把握するには ReferenceKinds と HasMixedReferenceKinds を
    // 併読する（issue #501）。scalar は既存 consumer の後方互換のため残す。
    public string ReferenceKind { get; set; } = string.Empty;
    public IReadOnlyList<string> ReferenceKinds { get; set; } = Array.Empty<string>();
    public bool HasMixedReferenceKinds { get; set; }
    public IReadOnlyDictionary<string, int> ReferenceKindCounts { get; set; } = new Dictionary<string, int>();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AggregateTruncated { get; set; }
    public double ReferenceWeightScore { get; set; }
    public int FirstLine { get; set; }
    public int FirstColumn { get; set; }
    [JsonIgnore]
    public int? FirstLength { get; set; }
    public int ReferenceCount { get; set; }
    public bool HasSelfReference { get; set; }
    public bool HasMutualRecursion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BodyContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BodyContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BodyContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? BodyContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CallsiteContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CallsiteContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? CallsiteContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteColumn { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteSelection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteOmittedReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteContentUnavailableReason { get; set; }
}

public class CalleeResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? CallerKind { get; set; }
    public string? CallerName { get; set; }
    public string CalleeName { get; set; } = string.Empty;
    public string ReferenceKind { get; set; } = string.Empty;
    public IReadOnlyList<string> ReferenceKinds { get; set; } = Array.Empty<string>();
    public bool HasMixedReferenceKinds { get; set; }
    public IReadOnlyDictionary<string, int> ReferenceKindCounts { get; set; } = new Dictionary<string, int>();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AggregateTruncated { get; set; }
    public double ReferenceWeightScore { get; set; }
    public int FirstLine { get; set; }
    // The representative call-site span is explicit so location consumers do not
    // reconstruct a column from display text. Legacy rows can retain a null column.
    // 代表 call-site の span を明示し、location consumer が表示文字列から列を
    // 復元しないようにする。legacy row の列は null のまま保持できる。
    public int? FirstColumn { get; set; }
    public int? FirstLength { get; set; }
    public int ReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BodyContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BodyContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BodyContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? BodyContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CallsiteContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CallsiteContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? CallsiteContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteColumn { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteSelection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteOmittedReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteContentUnavailableReason { get; set; }
}

public class ImpactResult
{
    public string ResultKind { get; set; } = ImpactResultKinds.Graph;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? CallerKind { get; set; }
    public string? CallerName { get; set; }
    public string CalleeName { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CallerSymbolId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CalleeSymbolId { get; set; }
    public int Depth { get; set; }
    public int FirstLine { get; set; }
    [JsonIgnore]
    public int? FirstColumn { get; set; }
    [JsonIgnore]
    public int? FirstLength { get; set; }
    public int ReferenceCount { get; set; }
    public string ReferenceKind { get; set; } = string.Empty;
    public IReadOnlyList<string> ReferenceKinds { get; set; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, int> ReferenceKindCounts { get; set; } = new Dictionary<string, int>();
    // Optional list of distinct shortest call paths from the resolved root symbol
    // through any intermediates to this caller. Each inner list is ordered
    // [resolvedRoot, intermediate..., thisCallerName]. Populated only when the
    // caller explicitly opts in (impact --with-paths) so default JSON stays compact;
    // null when the caller did not request paths. See issue #1536.
    // ルートシンボルから本 caller までの推移呼び出し経路（同 BFS 深さで収束する
    // 複数経路を保持）。各経路は [resolvedRoot, intermediate..., thisCallerName]
    // の順で並ぶ。impact --with-paths のときのみ populate される。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<List<string>>? Paths { get; set; }
    // Structured counterpart to Paths. Each hop carries definition-site and family
    // metadata so same-name or partial symbols are distinguishable without parsing names.
    // Paths の構造化版。各 hop に definition-site と family metadata を付け、
    // 同名 symbol や partial symbol を名前だけで判別しなくて済むようにする。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<List<ImpactPathNode>>? PathDetails { get; set; }
    // True when the caller has more distinct shortest paths than the per-row cap kept here.
    // 同一 caller に対して保持上限を超える別経路が存在する場合に true。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PathsTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BodyContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BodyContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BodyEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BodyContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? BodyContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteContent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CallsiteContentTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteRequestedStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteRequestedEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEffectiveStartLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteEffectiveEndLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CallsiteContentTruncationReasons { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcerptRecoveryHint? CallsiteContentRecovery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteColumn { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteSelection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CallsiteOmittedReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallsiteContentUnavailableReason { get; set; }
}

public class ImpactPathNode
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SymbolId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public string? Lang { get; set; }
    public string? DefinitionPath { get; set; }
    public int? DefinitionLine { get; set; }
    public string? Container { get; set; }
    public string? FamilyKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialFamilyId { get; set; }
    public string? LogicalTargetKey { get; set; }
    public string? ReferencePath { get; set; }
    public int? ReferenceLine { get; set; }
}

public static class ImpactResultKinds
{
    public const string Graph = "graph";
    public const string FileHeuristic = "file_heuristic";
}

public class ImpactAnalysisResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Query { get; set; } = string.Empty;
    public string ResolvedName { get; set; } = string.Empty;
    public string ImpactMode { get; set; } = "callers";
    public bool Heuristic { get; set; }
    public int MaxDepth { get; set; }
    public int DefinitionCount { get; set; }
    public int DefinitionFileCount { get; set; }
    [JsonIgnore]
    public int LogicalDefinitionCount { get; set; }
    public int HintCount { get; set; }
    public bool HasClassLikeDefinitions { get; set; }
    public bool HasMultipleDefinitions { get; set; }
    public bool HasMultipleDefinitionFiles { get; set; }
    public string TraversalRootScope { get; set; } = "symbol";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraversalPartialFamilyId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartialFamilyMemberCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartialFamilyMemberRootCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartialFamilyMemberRootLimit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PartialFamilyMemberRootTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartialFamilyMemberRootOmitted { get; set; }
    [JsonIgnore]
    internal bool CountIsAuthoritative => !Truncated && PartialFamilyMemberRootTruncated != true;
    public List<SymbolResult> Definitions { get; set; } = [];
    public List<ImpactResult> Callers { get; set; } = [];
    public List<FileDependencyResult> FileImpacts { get; set; } = [];
    public bool Truncated { get; set; }
    /// <summary>
    /// Explains why the impact traversal stopped. Known values are defined in
    /// <see cref="ImpactTerminationReasons"/>. Issue #1883.
    /// </summary>
    [JsonPropertyName("termination_reason")]
    public string TerminationReason { get; set; } = ImpactTerminationReasons.Completed;
    [JsonPropertyName("cycle_detected")]
    public bool CycleDetected { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ImpactCycleResult>? Cycles { get; set; }
    /// <summary>
    /// Distinguishes between truncation kinds when <see cref="Truncated"/> is true so callers
    /// can decide whether to retry with a higher <c>--limit</c> or treat the input graph as
    /// pathological. Known values:
    /// <list type="bullet">
    /// <item><c>user_limit</c>: the caller-supplied <c>--limit</c> was reached. Raising
    /// <c>--limit</c> will return more results.</item>
    /// <item><c>safety_cap</c>: an internal safety cap fired (e.g. the per-symbol fetch
    /// iteration ceiling). The graph is likely cyclic / runaway; raising <c>--limit</c>
    /// alone will not help.</item>
    /// </list>
    /// <c>null</c> whenever <see cref="Truncated"/> is false. Issue #1533.
    /// </summary>
    [JsonPropertyName("truncated_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TruncatedReason { get; set; }
    public bool GraphTableAvailable { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ZeroResultReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ImpactFailureChain { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SuggestionType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Suggestion { get; set; }
}

/// <summary>
/// Canonical <c>truncated_reason</c> values shared between <see cref="ImpactAnalysisResult"/>,
/// CLI JSON, and MCP output. Issue #1533.
/// </summary>
public static class ImpactTruncatedReasons
{
    /// <summary>User-supplied <c>--limit</c> reached.</summary>
    public const string UserLimit = "user_limit";

    /// <summary>Internal safety cap fired on a pathological graph.</summary>
    public const string SafetyCap = "safety_cap";

    /// <summary>Path/cycle parent state exceeded the traversal memory budget.</summary>
    public const string GraphStateBudget = "graph_state_budget";

    /// <summary>Boundary caller probing exceeded its max-depth scan budget.</summary>
    public const string BoundaryProbeBudget = "boundary_probe_budget";
}

public static class ImpactTerminationReasons
{
    public const string Completed = "completed";
    public const string MaxDepthReached = "max_depth_reached";
    public const string CycleDetected = "cycle_detected";
    public const string RowLimitTruncated = "row_limit_truncated";
    public const string SafetyCap = "safety_cap";
    public const string GraphStateBudget = "graph_state_budget";
    public const string BoundaryProbeBudget = "boundary_probe_budget";
    public const string Cancelled = "cancelled";
}

public class ImpactCycleResult
{
    public List<string> Members { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ImpactCycleMemberResult>? MemberIdentities { get; set; }
}

public class ImpactCycleMemberResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SymbolId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class StatusDatabasePermissionDiagnostic
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";
    [JsonPropertyName("target")]
    public string Target { get; set; } = "";
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
    [JsonPropertyName("recommended_action")]
    public string RecommendedAction { get; set; } = "";
}

public sealed class StatusQueryContext
{
    [JsonPropertyName("check_mode")]
    public string CheckMode { get; set; } = "";
    [JsonPropertyName("stale_after_seconds")]
    public long StaleAfterSeconds { get; set; }
}

/// <summary>
/// Runtime diagnostics for the Git executable selected by cdidx.
/// cdidx が選択した Git 実行ファイルの runtime 診断。
/// </summary>
public sealed record GitExecutableStatus(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("path")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Path,
    [property: JsonPropertyName("owner_only_writable")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? OwnerOnlyWritable,
    [property: JsonPropertyName("unix_mode")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? UnixMode,
    [property: JsonPropertyName("executable")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Executable,
    [property: JsonPropertyName("owner")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Owner,
    [property: JsonPropertyName("owner_trusted")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? OwnerTrusted,
    [property: JsonPropertyName("ancestor_directories_trusted")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? AncestorDirectoriesTrusted);

/// <summary>
/// Bounded per-object page attribution for SQLite status diagnostics.
/// SQLite status 診断向けの件数上限付きオブジェクト別ページ内訳。
/// </summary>
public sealed class StatusDatabaseObjectSize
{
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("object_type")]
    public string ObjectType { get; set; } = string.Empty;
    [JsonPropertyName("name_redacted_or_truncated")]
    public bool NameRedactedOrTruncated { get; set; }
    [JsonPropertyName("page_bytes")]
    public long PageBytes { get; set; }
    [JsonPropertyName("payload_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PayloadBytes { get; set; }
    [JsonPropertyName("unused_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UnusedBytes { get; set; }
}

/// <summary>
/// Read-only reconciliation of SQLite logical pages and physical database files.
/// SQLite の論理ページと物理 database file を読み取り専用で再照合した結果。
/// </summary>
public sealed class StatusDatabaseSizeAttribution
{
    public bool Available { get; set; }
    public string Measurement { get; set; } = "unavailable";
    [JsonPropertyName("unavailable_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnavailableReason { get; set; }
    [JsonPropertyName("page_size_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PageSizeBytes { get; set; }
    [JsonPropertyName("page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PageCount { get; set; }
    [JsonPropertyName("logical_database_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LogicalDatabaseBytes { get; set; }
    [JsonPropertyName("main_file_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MainFileBytes { get; set; }
    [JsonPropertyName("wal_file_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalFileBytes { get; set; }
    [JsonPropertyName("shm_file_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ShmFileBytes { get; set; }
    [JsonPropertyName("physical_file_set_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PhysicalFileSetBytes { get; set; }
    [JsonPropertyName("allocated_object_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AllocatedObjectBytes { get; set; }
    [JsonPropertyName("table_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TableBytes { get; set; }
    [JsonPropertyName("index_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? IndexBytes { get; set; }
    [JsonPropertyName("other_object_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OtherObjectBytes { get; set; }
    [JsonPropertyName("internal_page_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? InternalPageBytes { get; set; }
    [JsonPropertyName("leaf_page_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LeafPageBytes { get; set; }
    [JsonPropertyName("overflow_page_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OverflowPageBytes { get; set; }
    [JsonPropertyName("other_page_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OtherPageBytes { get; set; }
    [JsonPropertyName("payload_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PayloadBytes { get; set; }
    [JsonPropertyName("unused_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UnusedBytes { get; set; }
    [JsonPropertyName("structural_overhead_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StructuralOverheadBytes { get; set; }
    [JsonPropertyName("freelist_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FreelistBytes { get; set; }
    [JsonPropertyName("unexplained_residual_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UnexplainedResidualBytes { get; set; }
    [JsonPropertyName("object_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ObjectCount { get; set; }
    [JsonPropertyName("top_object_limit")]
    public int TopObjectLimit { get; set; }
    [JsonPropertyName("top_objects_truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TopObjectsTruncated { get; set; }
    [JsonPropertyName("top_objects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatusDatabaseObjectSize>? TopObjects { get; set; }
}

public class StatusResult
{
    internal const string SqliteConnectionPolicyJsonFieldName = "sqlite_connection_policy";

    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public long Files { get; set; }
    public long Chunks { get; set; }
    public long Symbols { get; set; }
    public long References { get; set; }
    /// <summary>
    /// Number of non-indexed files from the most recent successful full-repository scan whose
    /// non-empty extension did not map to a known language. Null on legacy DBs or before the
    /// current scanner has stamped this coverage signal (Issue #1585).
    /// 直近成功した全体 scan で、非空の拡張子が既知言語に対応しなかった未 index ファイル数。
    /// 旧 DB や現行 scanner による stamp 前は null。
    /// </summary>
    [JsonPropertyName("unknown_extension_file_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UnknownExtensionFileCount { get; set; }
    [JsonPropertyName("unknown_extension_files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? UnknownExtensionFiles { get; set; }
    [JsonPropertyName("unknown_extension_files_truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UnknownExtensionFilesTruncated { get; set; }
    [JsonPropertyName("unknown_extension_file_path_limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UnknownExtensionFilePathLimit { get; set; }
    [JsonPropertyName("unknown_extension_extension_counts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? UnknownExtensionExtensionCounts { get; set; }
    [JsonPropertyName("unknown_extension_category_counts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? UnknownExtensionCategoryCounts { get; set; }
    [JsonPropertyName("unknown_extension_groups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatusUnknownExtensionGroup>? UnknownExtensionGroups { get; set; }
    public DateTime? IndexedAt { get; set; }
    /// <summary>
    /// Timestamp of the most recent successful index/update run that freshened workspace
    /// state, including partial and no-op updates. This can be newer than <see cref="IndexedAt"/>,
    /// which is derived from indexed file rows and may not move when an update only confirms
    /// freshness without rewriting file rows (#3238).
    /// partial / no-op update も含め、workspace 鮮度を最後に確認・更新した成功 index 実行時刻。
    /// file row 由来の IndexedAt より新しい場合がある。
    /// </summary>
    [JsonPropertyName("last_workspace_freshened_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastWorkspaceFreshenedAt { get; set; }
    public DateTime? LatestModified { get; set; }
    public string? ProjectRoot { get; set; }
    [JsonPropertyName("data_dir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataDir { get; set; }
    [JsonPropertyName("data_dir_source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataDirSource { get; set; }
    /// <summary>
    /// POSIX permission bits for the directory containing the active CodeIndex database,
    /// formatted as an octal string such as "0700". Null on Windows, URI databases, missing
    /// directories, or platforms that do not expose Unix file modes. Issue #1793.
    /// 現在の CodeIndex DB を含むディレクトリの POSIX 権限。Windows / URI DB / 不在時は null。
    /// </summary>
    [JsonPropertyName("data_dir_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataDirMode { get; set; }
    [JsonPropertyName("db_file_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DbFileMode { get; set; }
    [JsonPropertyName("database_permission_policy")]
    public string DatabasePermissionPolicy { get; set; } = "best_effort";
    [JsonPropertyName("database_permission_diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatusDatabasePermissionDiagnostic>? DatabasePermissionDiagnostics { get; set; }
    [JsonPropertyName("read_only_fallback")]
    public bool ReadOnlyFallback { get; set; }
    [JsonPropertyName("wal_checkpoint_attempted")]
    public bool WalCheckpointAttempted { get; set; }
    [JsonPropertyName("wal_checkpoint_succeeded")]
    public bool WalCheckpointSucceeded { get; set; }
    [JsonPropertyName("read_only_immutable_fallback")]
    public bool ReadOnlyImmutableFallback { get; set; }
    [JsonPropertyName("wal_checkpoint_skipped_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WalCheckpointSkippedReason { get; set; }
    [JsonPropertyName("wal_checkpoint_failure_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WalCheckpointFailureReason { get; set; }
    [JsonPropertyName("wal_checkpoint_busy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalCheckpointBusy { get; set; }
    [JsonPropertyName("wal_checkpoint_log_page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalCheckpointLogPageCount { get; set; }
    [JsonPropertyName("wal_checkpoint_checkpointed_page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalCheckpointCheckpointedPageCount { get; set; }
    [JsonPropertyName("wal_checkpoint_remaining_page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalCheckpointRemainingPageCount { get; set; }
    [JsonPropertyName("wal_stale_snapshot_risk")]
    public bool WalStaleSnapshotRisk { get; set; }
    [JsonPropertyName("wal_stale_snapshot_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WalStaleSnapshotReason { get; set; }
    [JsonPropertyName(SqliteConnectionPolicyJsonFieldName)]
    public StatusSqliteConnectionPolicy SqliteConnectionPolicy { get; set; } = new();
    public string? GitHead { get; set; }
    public bool? GitIsDirty { get; set; }
    /// <summary>
    /// Best-effort Linux mandatory-access-control profile for the running process, such as
    /// `apparmor:snap.cdidx.cdidx` or `selinux:user_u:user_r:user_t:s0`. Null on non-Linux
    /// hosts, unconstrained processes, or when `/proc/self/attr/*` cannot be read (#1768).
    /// 実行中プロセスの Linux MAC profile。非 Linux / 非制限 / 読み取り不可では null。
    /// </summary>
    [JsonPropertyName("mac_profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MacProfile { get; set; }
    [JsonPropertyName("mac_profile_diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MacProfileDiagnostic>? MacProfileDiagnostics { get; set; }
    /// <summary>
    /// Git HEAD commit captured at the end of the most recent successful full-scan
    /// index run (Issue #1508). Current databases expose it as legacy compatibility
    /// provenance; HEAD freshness prefers <see cref="WorkspaceVerifiedHeadSha"/>.
    /// Null when the DB has no `indexed_head_commit` metadata.
    /// 直近 full-scan 成功時点で記録された git HEAD。現行 DB では互換 provenance として公開し、
    /// HEAD freshness は <see cref="WorkspaceVerifiedHeadSha"/> を優先する。
    /// </summary>
    [JsonPropertyName("indexed_head_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadCommit { get; set; }
    /// <summary>
    /// Git HEAD whose complete workspace contents were last verified by a full scan or a
    /// baseline-reconciled Git refresh. This provenance is distinct from the latest scoped
    /// update stamp in <see cref="IndexedHeadSha"/>. Issue #5054.
    /// full scan または基準差分を補完した Git refresh により、workspace 全体との一致を
    /// 最後に検証した Git HEAD。最新 scoped update の stamp とは別物。Issue #5054。
    /// </summary>
    [JsonPropertyName("workspace_verified_head_sha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceVerifiedHeadSha { get; set; }
    /// <summary>
    /// True when the whole-workspace verified HEAD (or its legacy full-scan fallback)
    /// differs from the runtime `GitHead`, indicating that a refresh is needed before
    /// freshness-sensitive results are trustworthy. Null when comparison is not possible.
    /// Issues #1512 and #5054.
    /// workspace 全体の検証済み HEAD（または旧 full-scan fallback）と runtime HEAD が
    /// 異なれば true。比較不能なら null。
    /// </summary>
    [JsonPropertyName("worktree_head_changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorktreeHeadChanged { get; set; }
    /// <summary>
    /// Full Git commit SHA stamped into `codeindex_meta` at the end of the last successful
    /// index run (full scan AND partial update). Distinct from <see cref="IndexedHeadCommit"/>
    /// above, which fires only on full scans (#1508 / #1512). This field drives the
    /// commit-drift count <see cref="CommitsAheadOfIndexedHead"/> so cross-session staleness
    /// is detectable regardless of update mode. Null on legacy DBs / non-git workspaces. #1509.
    /// 最後に成功した index 実行 (full scan / partial update 問わず) で記録された Git HEAD SHA。
    /// </summary>
    [JsonPropertyName("indexed_head_sha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadSha { get; set; }
    // Persisted HEAD provenance is captured by DbReader in one SQLite snapshot. These
    // non-contract fields let runtime Git enrichment derive branch/HEAD drift without
    // reopening the database and mixing generations. Issue #5054.
    // persisted HEAD provenance は DbReader の同一 SQLite snapshot で取得する。
    // runtime Git 補強時に DB を再読込して世代を混在させないための非公開情報。
    [JsonIgnore]
    internal bool HeadMetadataSnapshotCaptured { get; set; }
    [JsonIgnore]
    internal string? IndexedHeadCommitBranchSnapshot { get; set; }
    [JsonIgnore]
    internal bool IndexedHeadCommitBranchStampPresentSnapshot { get; set; }
    [JsonIgnore]
    internal bool IndexedHeadBranchStampPresentSnapshot { get; set; }
    /// <summary>
    /// Branch short name (e.g. `main`) captured at the same time as <see cref="IndexedHeadSha"/>.
    /// Null when the branch could not be resolved (detached HEAD) or the DB was indexed before
    /// issue #1509 introduced this metadata.
    /// 同 stamp 時のブランチ短縮名。detached HEAD・旧 DB では null。
    /// </summary>
    [JsonPropertyName("indexed_head_branch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadBranch { get; set; }
    [JsonPropertyName("indexed_head_timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? IndexedHeadTimestamp { get; set; }
    /// <summary>
    /// Number of commits the current Git HEAD is ahead of <see cref="IndexedHeadSha"/>.
    /// 0 means the index was built against the commit currently checked out. A positive
    /// number means the worktree has advanced since indexing. Null when the comparison
    /// is not meaningful (no stamp, non-linear history, git unavailable, etc.).
    /// 現在 HEAD が記録時 HEAD より何コミット進んでいるか。比較不能時は null。
    /// </summary>
    [JsonPropertyName("commits_ahead_of_indexed_head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CommitsAheadOfIndexedHead { get; set; }
    /// <summary>
    /// Compact machine-facing summary of the runtime HEAD, latest indexed HEAD stamp, legacy
    /// full-scan HEAD stamp, and optional `status --check` workspace comparison. This is
    /// additive context for automation; the individual legacy fields remain authoritative.
    /// runtime HEAD / 最新 index HEAD stamp / legacy full-scan HEAD stamp / 任意の
    /// `status --check` 比較をまとめた機械向け summary。
    /// </summary>
    [JsonPropertyName("head_freshness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusHeadFreshness? HeadFreshness => StatusHeadFreshness.FromStatus(this);
    public Dictionary<string, long> Languages { get; set; } = new();
    public Dictionary<string, long>? SymbolKinds { get; set; }
    [JsonPropertyName("symbol_kind_limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SymbolKindLimit { get; set; }
    [JsonPropertyName("symbol_kind_name_limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SymbolKindNameLimit { get; set; }
    [JsonPropertyName("symbol_kind_total_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SymbolKindTotalCount { get; set; }
    [JsonPropertyName("symbol_kind_omitted_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SymbolKindOmittedCount { get; set; }
    [JsonPropertyName("symbol_kind_names_truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SymbolKindNamesTruncated { get; set; }
    [JsonPropertyName("symbols_by_language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, long>>? SymbolsByLanguage { get; set; }
    [JsonPropertyName("symbols_by_language_kind_total_counts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? SymbolsByLanguageKindTotalCounts { get; set; }
    [JsonPropertyName("symbols_by_language_kind_omitted_counts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? SymbolsByLanguageKindOmittedCounts { get; set; }
    [JsonPropertyName("symbols_by_language_kind_names_truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SymbolsByLanguageKindNamesTruncated { get; set; }
    public List<string>? GraphSupportedLanguages { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PostExtractionHookStatus>? Hooks { get; set; }
    [JsonPropertyName("hook_diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PostExtractionHookDiagnostic>? HookDiagnostics { get; set; }
    [JsonPropertyName("trust_overrides")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtensionTrustOverride>? TrustOverrides { get; set; }
    [JsonPropertyName("git_executable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GitExecutableStatus? GitExecutable { get; set; }
    [JsonPropertyName("extractors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExtractorRegistryStatus? Extractors { get; set; }
    public string? Version { get; set; }
    [JsonPropertyName("update_check")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UpdateCheckResult? UpdateCheck { get; set; }
    /// <summary>
    /// One-line human-readable summary for quick orientation.
    /// クイックオリエンテーション用の1行サマリー。
    /// </summary>
    public string? Summary { get; set; }
    [JsonPropertyName("index_matches_workspace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IndexMatchesWorkspace { get; set; }
    [JsonPropertyName("workspace_check")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IndexFreshnessCheckResult? WorkspaceCheck { get; set; }
    [JsonPropertyName("failed_checks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FailedChecks { get; set; }
    [JsonPropertyName("repair_commands")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatusRepairCommand>? RepairCommands { get; set; }
    /// <summary>
    /// Effective status-check invocation mode and threshold. Null for ordinary status output.
    /// status check の有効な呼び出し mode としきい値。通常の status 出力では null。
    /// </summary>
    [JsonPropertyName("query_context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusQueryContext? QueryContext { get; set; }
    /// <summary>
    /// Effective age threshold, in seconds, used by `status --check` to explain stale-index
    /// warnings. Null when `--check` was not requested.
    /// `status --check` が stale 判定の説明に使った有効なしきい値（秒）。
    /// </summary>
    [JsonPropertyName("stale_after_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StaleAfterSeconds { get; set; }
    /// <summary>
    /// Age of the current index at `status --check` time, in seconds. Null when the DB has no
    /// indexed timestamp or `--check` was not requested.
    /// `status --check` 実行時点の index 経過秒数。timestamp が無い場合や未 check 時は null。
    /// </summary>
    [JsonPropertyName("index_age_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? IndexAgeSeconds { get; set; }
    /// <summary>
    /// True when the index exposes the full reference / validation tables. False signals a
    /// degraded read (legacy or read-only DB where TryMigrateForRead could not create
    /// symbol_references / file_issues), so a zero reference or issue count must not be
    /// trusted as a real "no callers" or "clean" signal.
    /// インデックスに参照／検証テーブルが揃っているかの信頼シグナル。false の場合、references
    /// や issues の 0 件は「本当に 0 件」なのか「テーブルが無いから 0 件」なのか区別できない。
    /// </summary>
    public bool GraphTableAvailable { get; set; } = true;
    [JsonPropertyName("graph_data_current")]
    public bool GraphDataCurrent { get; set; } = true;
    [JsonPropertyName("reference_extraction_limits")]
    public ReferenceExtractionSafetyLimits ReferenceExtractionLimits { get; set; } = new();
    [JsonPropertyName("reference_graph_complete")]
    public bool ReferenceGraphComplete { get; set; } = true;
    [JsonPropertyName("reference_graph_incomplete_reasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ReferenceGraphIncompleteReasons { get; set; }
    [JsonPropertyName("reference_extraction_cap_hits")]
    public ReferenceExtractionCapHitSummary ReferenceExtractionCapHits { get; set; } = new();
    public bool IssuesTableAvailable { get; set; } = true;
    [JsonPropertyName("file_issues_data_current")]
    public bool FileIssuesDataCurrent { get; set; } = true;
    [JsonPropertyName("migration_in_progress")]
    public bool MigrationInProgress { get; set; }
    [JsonPropertyName("index_complete")]
    public bool IndexComplete { get; set; } = true;
    [JsonPropertyName("index_incomplete_reasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IndexIncompleteReasons { get; set; }
    /// <summary>
    /// True when authoritative cross-file hotspot-family grouping metadata is current for every
    /// marker-capable language currently indexed in this DB. False means `hotspots` can still
    /// run, but duplicate-name families may be conservatively degraded. The degraded reason
    /// distinguishes legacy DBs that predate hotspot-family support, stale metadata, and
    /// indexes written while marker fingerprints were unavailable.
    /// 現在 index 済みの marker-capable 言語すべてで authoritative な hotspot-family metadata
    /// が最新なら true。false の間も `hotspots` は動くが、duplicate-name family は保守的
    /// fallback に縮退しうる。reason は legacy DB、古い metadata、marker fingerprint 未利用
    /// index を区別する。
    /// </summary>
    [JsonPropertyName("hotspot_family_ready")]
    public bool HotspotFamilyReady { get; set; } = true;
    [JsonPropertyName("hotspot_family_degraded_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HotspotFamilyDegradedReason { get; set; }
    [JsonPropertyName("language_readiness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, LanguageReadinessSignal>>? LanguageReadiness { get; set; }
    /// <summary>
    /// True when C# canonical symbol-name upgrades (for operators, conversion operators,
    /// indexers) have been applied to all indexed C# rows in this DB. False means exact-name
    /// lookup for those C# symbol families may still require an upgrade pass via `cdidx index .`.
    /// C# canonical symbol name 契約が DB 全体へ適用済みかどうか。
    /// </summary>
    [JsonPropertyName("csharp_symbol_name_ready")]
    public bool CSharpSymbolNameReady { get; set; } = true;
    /// <summary>
    /// True when every indexed C# class row carries an authoritative `is_metadata_target`
    /// value stamped under the current `metadata_target_version_csharp` contract from
    /// extractor facts plus the writer resolver. False means the `deps` / `impact`
    /// metadata-attribute edges fall back to the legacy `signature LIKE '%: %'` heuristic
    /// (or the `name LIKE '%Attribute'` suffix heuristic on truly-legacy DBs missing the
    /// `is_metadata_target` column). Run `cdidx index .` once to restamp the contract (#3524).
    /// true のとき deps / impact の metadata-attribute edge は extractor fact と writer resolver が
    /// stamp した persisted な `is_metadata_target` 列を使い、false のとき legacy heuristic 経路で縮退する。
    /// </summary>
    [JsonPropertyName("csharp_metadata_target_ready")]
    public bool CSharpMetadataTargetReady { get; set; } = true;
    [JsonPropertyName("csharp_metadata_target_degraded_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CSharpMetadataTargetDegradedReason { get; set; }
    /// <summary>
    /// True when every indexed SQL graph row was written under the current stored call-column /
    /// qualified-name contract. False means SQL graph/dependency readers may still return false
    /// negatives until `cdidx index .` rewrites unchanged SQL rows.
    /// SQL graph 行が current の call-column / qualified-name 契約で書かれていれば true。
    /// </summary>
    [JsonPropertyName("sql_graph_contract_ready")]
    public bool SqlGraphContractReady { get; set; } = true;
    [JsonPropertyName("sql_graph_contract_degraded_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlGraphContractDegradedReason { get; set; }
    /// <summary>
    /// True when every row in symbols / symbol_references has its name_folded column
    /// populated AND the FoldReadyFlag bit is set on the DB. False means `--exact` still
    /// falls back to ASCII `COLLATE NOCASE` (non-ASCII casing pairs like Ä/ä won't match).
    /// AI clients should prefer `cdidx backfill-fold` to upgrade an older DB without
    /// reparsing files; `cdidx index . --rebuild` remains the full-rescan fallback.
    /// true のとき --exact は Unicode fold 経路、false のとき ASCII NOCASE fallback。
    /// </summary>
    public bool FoldReady { get; set; }
    [JsonPropertyName("fold_ready_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FoldReadyReason { get; set; }
    [JsonPropertyName("degraded_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DegradedReason { get; set; }
    [JsonPropertyName("degraded_root_cause")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DegradedRootCause { get; set; }
    [JsonPropertyName("recommended_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedAction { get; set; }
    [JsonPropertyName("alternative_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AlternativeAction { get; set; }
    [JsonPropertyName("readiness_degradations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatusReadinessDegradation>? ReadinessDegradations { get; set; }
    /// <summary>
    /// The cdidx version string that wrote the most recent successful end-of-index pass
    /// for this DB, stamped from `codeindex_meta.cdidx_writer_version`. Null on legacy
    /// DBs that predate the audit-trail stamp (Issue #1515).
    /// 最後に index 成功末尾を書き込んだ cdidx の version 文字列。stamp が無い旧 DB では null。
    /// </summary>
    [JsonPropertyName("index_writer_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexWriterVersion { get; set; }
    /// <summary>
    /// True when this DB persists at least one contract version that is strictly newer
    /// than the constants compiled into this cdidx binary, signaling the DB was written
    /// by a newer cdidx and existing string.Equals readiness gates are silently
    /// degrading. The reason names the specific contracts that exceed (Issue #1515).
    /// より新しい cdidx で書かれた DB を旧 cdidx が開いたときに true。
    /// </summary>
    [JsonPropertyName("index_newer_than_reader")]
    public bool IndexNewerThanReader { get; set; }
    [JsonPropertyName("index_newer_than_reader_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexNewerThanReaderReason { get; set; }
    /// <summary>
    /// True when the workspace filesystem this index was built on treats path names as
    /// case-sensitive (e.g. case-sensitive APFS on macOS, case-sensitive NTFS via WSL,
    /// case-sensitive ReFS, ext4/btrfs on Linux). False when names are folded
    /// case-insensitively (default APFS / HFS+ on macOS, default NTFS on Windows).
    /// Null on legacy DBs that predate Issue #1546's probe stamp. Stamped on every
    /// successful index pass (full scan and partial update).
    /// 直近の index 実行時にワークスペース FS が大小区別したか。case-sensitive なら true、
    /// case-insensitive なら false。stamp の無い旧 DB では null。Issue #1546。
    /// </summary>
    [JsonPropertyName("path_case_sensitive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PathCaseSensitive { get; set; }
    [JsonIgnore]
    public string? IndexedFollowSymlinksPolicy { get; set; }
    /// <summary>
    /// Connection-level SQLite PRAGMA values that affect WAL durability and checkpoint
    /// behavior for the current reader. Exposed so automation can verify the index DB is
    /// running under cdidx's documented WAL policy (#1925).
    /// 現在の reader 接続に適用されている WAL durability / checkpoint 関連 PRAGMA。
    /// </summary>
    [JsonPropertyName("db_pragma_settings")]
    public StatusDbPragmaSettings DbPragmaSettings { get; set; } = new();
    [JsonPropertyName("prepared_command_cache")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusPreparedCommandCache? PreparedCommandCache { get; set; }
    [JsonPropertyName("maintenance_guidance")]
    public StatusMaintenanceGuidance MaintenanceGuidance { get; set; } = new();
    [JsonPropertyName("db_size_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DbSizeBytes { get; set; }
    [JsonPropertyName("wal_size_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalSizeBytes { get; set; }
    [JsonPropertyName("database_size_attribution")]
    public StatusDatabaseSizeAttribution DatabaseSizeAttribution { get; set; } = new();
    [JsonPropertyName("process")]
    public StatusProcessMetrics Process { get; set; } = StatusProcessMetrics.Capture();
    [JsonPropertyName("last_index_run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusLastIndexRun? LastIndexRun { get; set; }
    [JsonPropertyName("last_failed_or_partial_index_run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusFailedOrPartialIndexRun? LastFailedOrPartialIndexRun { get; set; }
}

public sealed class StatusHeadFreshness
{
    public string State { get; init; } = "unchecked";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }
    [JsonPropertyName("state_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StateReason { get; init; }
    [JsonPropertyName("runtime_head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeHead { get; init; }
    [JsonPropertyName("indexed_head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHead { get; init; }
    [JsonPropertyName("indexed_head_source")]
    public string IndexedHeadSource { get; init; } = "unavailable";
    [JsonPropertyName("legacy_full_scan_head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyFullScanHead { get; init; }
    [JsonPropertyName("workspace_verified_head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceVerifiedHead { get; init; }
    [JsonPropertyName("latest_index_head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestIndexHead { get; init; }
    [JsonPropertyName("indexed_head_branch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadBranch { get; init; }
    [JsonPropertyName("indexed_head_timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? IndexedHeadTimestamp { get; init; }
    [JsonPropertyName("workspace_check_indexed_head_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceCheckIndexedHeadCommit { get; init; }
    [JsonPropertyName("workspace_check_workspace_head_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceCheckWorkspaceHeadCommit { get; init; }
    [JsonPropertyName("workspace_matches_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorkspaceMatchesIndex { get; init; }
    [JsonPropertyName("worktree_head_changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorktreeHeadChanged { get; init; }
    [JsonPropertyName("commits_ahead_of_indexed_head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CommitsAheadOfIndexedHead { get; init; }

    public static StatusHeadFreshness? FromStatus(StatusResult status)
    {
        if (!HasHeadFreshnessSignal(status))
            return null;

        var indexedHead = NullIfWhiteSpace(status.WorkspaceVerifiedHeadSha)
            ?? NullIfWhiteSpace(status.IndexedHeadCommit)
            ?? NullIfWhiteSpace(status.IndexedHeadSha);
        var latestIndexHead = NullIfWhiteSpace(status.IndexedHeadSha);
        var indexedHeadMatchesLatest = indexedHead != null
            && string.Equals(indexedHead, latestIndexHead, StringComparison.OrdinalIgnoreCase);
        var workspaceCheck = status.WorkspaceCheck;

        return new StatusHeadFreshness
        {
            State = ResolveState(status, workspaceCheck),
            Scope = "workspace",
            StateReason = ResolveStateReason(status, workspaceCheck),
            RuntimeHead = NullIfWhiteSpace(status.GitHead),
            IndexedHead = indexedHead,
            IndexedHeadSource = ResolveIndexedHeadSource(status),
            LegacyFullScanHead = NullIfWhiteSpace(status.IndexedHeadCommit),
            WorkspaceVerifiedHead = NullIfWhiteSpace(status.WorkspaceVerifiedHeadSha),
            LatestIndexHead = latestIndexHead,
            IndexedHeadBranch = indexedHeadMatchesLatest ? NullIfWhiteSpace(status.IndexedHeadBranch) : null,
            IndexedHeadTimestamp = indexedHeadMatchesLatest ? status.IndexedHeadTimestamp : null,
            WorkspaceCheckIndexedHeadCommit = NullIfWhiteSpace(workspaceCheck?.IndexedHeadCommit),
            WorkspaceCheckWorkspaceHeadCommit = NullIfWhiteSpace(workspaceCheck?.WorkspaceHeadCommit),
            WorkspaceMatchesIndex = status.IndexMatchesWorkspace ?? workspaceCheck?.MatchesWorkspace,
            WorktreeHeadChanged = status.WorktreeHeadChanged,
            CommitsAheadOfIndexedHead = indexedHeadMatchesLatest ? status.CommitsAheadOfIndexedHead : null,
        };
    }

    internal static StatusHeadFreshness? FromMap(RepoMapResult map)
    {
        if (string.IsNullOrWhiteSpace(map.GitHead)
            && string.IsNullOrWhiteSpace(map.IndexedHeadSha)
            && string.IsNullOrWhiteSpace(map.WorkspaceVerifiedHeadSha)
            && string.IsNullOrWhiteSpace(map.IndexedHeadCommit)
            && string.IsNullOrWhiteSpace(map.IndexedHeadBranch)
            && !map.IndexedHeadTimestamp.HasValue
            && !map.WorktreeHeadChanged.HasValue
            && !map.CommitsAheadOfIndexedHead.HasValue)
        {
            return null;
        }

        var indexedHead = NullIfWhiteSpace(map.WorkspaceVerifiedHeadSha)
            ?? NullIfWhiteSpace(map.IndexedHeadCommit)
            ?? NullIfWhiteSpace(map.IndexedHeadSha);
        var latestIndexHead = NullIfWhiteSpace(map.IndexedHeadSha);
        var indexedHeadMatchesLatest = indexedHead != null
            && string.Equals(indexedHead, latestIndexHead, StringComparison.OrdinalIgnoreCase);
        return new StatusHeadFreshness
        {
            State = map.WorktreeHeadChanged switch
            {
                true => "head_changed",
                false => "head_current",
                _ => "unchecked",
            },
            Scope = "workspace",
            StateReason = map.WorktreeHeadChanged switch
            {
                true => "worktree_head_changed",
                false => "head_current",
                _ => null,
            },
            RuntimeHead = NullIfWhiteSpace(map.GitHead),
            IndexedHead = indexedHead,
            IndexedHeadSource = !string.IsNullOrWhiteSpace(map.WorkspaceVerifiedHeadSha)
                ? "workspace_verified"
                : !string.IsNullOrWhiteSpace(map.IndexedHeadCommit)
                    ? "legacy_full_scan"
                    : !string.IsNullOrWhiteSpace(map.IndexedHeadSha)
                        ? "latest_index"
                        : "unavailable",
            LegacyFullScanHead = NullIfWhiteSpace(map.IndexedHeadCommit),
            WorkspaceVerifiedHead = NullIfWhiteSpace(map.WorkspaceVerifiedHeadSha),
            LatestIndexHead = latestIndexHead,
            IndexedHeadBranch = indexedHeadMatchesLatest ? NullIfWhiteSpace(map.IndexedHeadBranch) : null,
            IndexedHeadTimestamp = indexedHeadMatchesLatest ? map.IndexedHeadTimestamp : null,
            WorktreeHeadChanged = map.WorktreeHeadChanged,
            CommitsAheadOfIndexedHead = indexedHeadMatchesLatest ? map.CommitsAheadOfIndexedHead : null,
        };
    }

    private static bool HasHeadFreshnessSignal(StatusResult status) =>
        status.WorkspaceCheck is not null
        || !string.IsNullOrWhiteSpace(status.GitHead)
        || !string.IsNullOrWhiteSpace(status.IndexedHeadSha)
        || !string.IsNullOrWhiteSpace(status.WorkspaceVerifiedHeadSha)
        || !string.IsNullOrWhiteSpace(status.IndexedHeadCommit)
        || !string.IsNullOrWhiteSpace(status.IndexedHeadBranch)
        || status.IndexedHeadTimestamp.HasValue
        || status.WorktreeHeadChanged.HasValue
        || status.CommitsAheadOfIndexedHead.HasValue
        || status.IndexMatchesWorkspace.HasValue;

    private static string ResolveState(StatusResult status, IndexFreshnessCheckResult? workspaceCheck)
    {
        if (workspaceCheck is not null)
        {
            if (!workspaceCheck.Checked)
                return "check_unavailable";
            if (!workspaceCheck.MatchesWorkspace)
                return IsHeadChanged(status, workspaceCheck)
                    ? "head_changed"
                    : status.IndexComplete ? "stale" : "stale_and_incomplete";
            return status.IndexComplete ? "fresh" : "fresh_but_incomplete";
        }

        if (status.WorktreeHeadChanged == true)
            return "head_changed";
        if (status.WorktreeHeadChanged == false)
            return "head_current";
        return "unchecked";
    }

    private static string? ResolveStateReason(StatusResult status, IndexFreshnessCheckResult? workspaceCheck)
    {
        if (!status.IndexComplete && workspaceCheck?.Checked == true && workspaceCheck.MatchesWorkspace)
            return "index_incomplete";
        if (workspaceCheck is not null)
            return string.IsNullOrWhiteSpace(workspaceCheck.Reason) ? null : workspaceCheck.Reason;
        if (status.WorktreeHeadChanged == true)
            return "worktree_head_changed";
        if (status.WorktreeHeadChanged == false)
            return "head_current";
        return null;
    }

    private static bool IsHeadChanged(StatusResult status, IndexFreshnessCheckResult workspaceCheck) =>
        workspaceCheck.HeadChanged
        || status.WorktreeHeadChanged == true
        || string.Equals(workspaceCheck.Reason, "head_changed", StringComparison.Ordinal);

    private static string ResolveIndexedHeadSource(StatusResult status)
    {
        if (!string.IsNullOrWhiteSpace(status.WorkspaceVerifiedHeadSha))
            return "workspace_verified";
        if (!string.IsNullOrWhiteSpace(status.IndexedHeadCommit))
            return "legacy_full_scan";
        if (!string.IsNullOrWhiteSpace(status.IndexedHeadSha))
            return "latest_index";
        return "unavailable";
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class StatusSqliteConnectionPolicy
{
    [JsonPropertyName("active_mode")]
    public string ActiveMode { get; set; } = string.Empty;
    [JsonPropertyName("open_mode")]
    public string OpenMode { get; set; } = string.Empty;
    [JsonPropertyName("pooling")]
    public bool Pooling { get; set; } = true;
    [JsonPropertyName("immutable_uri")]
    public bool ImmutableUri { get; set; }
    [JsonPropertyName("command_timeout_seconds")]
    public int CommandTimeoutSeconds { get; set; }
    [JsonPropertyName("long_running_commands_require_cancellation")]
    public bool LongRunningCommandsRequireCancellation { get; set; }
    [JsonPropertyName("read_only_fallback")]
    public bool ReadOnlyFallback { get; set; }
    [JsonPropertyName("wal_checkpoint_attempted")]
    public bool WalCheckpointAttempted { get; set; }
    [JsonPropertyName("wal_checkpoint_succeeded")]
    public bool WalCheckpointSucceeded { get; set; }
    [JsonPropertyName("read_only_immutable_fallback")]
    public bool ReadOnlyImmutableFallback { get; set; }
    [JsonPropertyName("wal_checkpoint_skipped_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WalCheckpointSkippedReason { get; set; }
    [JsonPropertyName("wal_checkpoint_failure_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WalCheckpointFailureReason { get; set; }
    [JsonPropertyName("wal_checkpoint_busy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalCheckpointBusy { get; set; }
    [JsonPropertyName("wal_checkpoint_log_page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalCheckpointLogPageCount { get; set; }
    [JsonPropertyName("wal_checkpoint_checkpointed_page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalCheckpointCheckpointedPageCount { get; set; }
    [JsonPropertyName("wal_checkpoint_remaining_page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WalCheckpointRemainingPageCount { get; set; }
    [JsonPropertyName("wal_stale_snapshot_risk")]
    public bool WalStaleSnapshotRisk { get; set; }
    [JsonPropertyName("wal_stale_snapshot_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WalStaleSnapshotReason { get; set; }
}

public sealed class StatusProcessMetrics
{
    [JsonPropertyName("heap_bytes")]
    public long HeapBytes { get; set; }
    [JsonPropertyName("gc_heap_size_bytes")]
    public long GcHeapSizeBytes { get; set; }
    [JsonPropertyName("gc_gen0_count")]
    public int GcGen0Count { get; set; }
    [JsonPropertyName("gc_gen1_count")]
    public int GcGen1Count { get; set; }
    [JsonPropertyName("gc_gen2_count")]
    public int GcGen2Count { get; set; }
    [JsonPropertyName("working_set_bytes")]
    public long WorkingSetBytes { get; set; }

    public static StatusProcessMetrics Capture()
    {
        var snapshot = ProcessMemorySnapshot.Capture();
        return new StatusProcessMetrics
        {
            HeapBytes = snapshot.HeapBytes,
            GcHeapSizeBytes = snapshot.GcHeapSizeBytes,
            GcGen0Count = snapshot.Gen0Collections,
            GcGen1Count = snapshot.Gen1Collections,
            GcGen2Count = snapshot.Gen2Collections,
            WorkingSetBytes = snapshot.WorkingSetBytes,
        };
    }
}

public sealed class StatusPreparedCommandCache
{
    public int Count { get; set; }
    public int Capacity { get; set; }
    [JsonPropertyName("hit_count")]
    public long HitCount { get; set; }
    [JsonPropertyName("miss_count")]
    public long MissCount { get; set; }
    [JsonPropertyName("eviction_count")]
    public long EvictionCount { get; set; }
}

public sealed class StatusLastIndexRun
{
    public string? Mode { get; set; }
    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }
    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; set; }
    [JsonPropertyName("files_scanned")]
    public long? FilesScanned { get; set; }
    [JsonPropertyName("files_skipped")]
    public long? FilesSkipped { get; set; }
    [JsonPropertyName("parse_errors")]
    public long? ParseErrors { get; set; }
    [JsonPropertyName("bytes_read")]
    public long? BytesRead { get; set; }
    [JsonPropertyName("bytes_read_skipped_file_count")]
    public long? BytesReadSkippedFileCount { get; set; }
    [JsonPropertyName("bytes_read_incomplete")]
    public bool? BytesReadIncomplete { get; set; }
    [JsonPropertyName("rows_upserted")]
    public long? RowsUpserted { get; set; }
    [JsonPropertyName("rows_deleted")]
    public long? RowsDeleted { get; set; }
    [JsonPropertyName("peak_memory_mb")]
    public long? PeakMemoryMb { get; set; }
    [JsonPropertyName("diagnostics")]
    public List<string>? Diagnostics { get; set; }
    [JsonPropertyName("diagnostic_count")]
    public long? DiagnosticCount { get; set; }
    [JsonPropertyName("diagnostics_truncated")]
    public bool? DiagnosticsTruncated { get; set; }
    [JsonPropertyName("reference_extraction_cap_hits")]
    public ReferenceExtractionCapHitSummary? ReferenceExtractionCapHits { get; set; }
    [JsonPropertyName("rebuild_reclaim")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusRebuildReclaim? RebuildReclaim { get; set; }
}

/// <summary>
/// Bounded telemetry for the thresholded free-page reclaim that follows a successful rebuild.
/// 成功した rebuild 後にしきい値付きで行う free-page 回収の bounded telemetry。
/// </summary>
public sealed class StatusRebuildReclaim
{
    public string State { get; set; } = "not_needed";
    public string Reason { get; set; } = "freelist_below_threshold";
    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }
    [JsonPropertyName("page_size_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PageSizeBytes { get; set; }
    [JsonPropertyName("page_count_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PageCountBefore { get; set; }
    [JsonPropertyName("freelist_count_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FreelistCountBefore { get; set; }
    [JsonPropertyName("freelist_ratio_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FreelistRatioBefore { get; set; }
    [JsonPropertyName("freelist_threshold_ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FreelistThresholdRatio { get; set; }
    [JsonPropertyName("estimated_bytes_reclaimable_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EstimatedBytesReclaimableBefore { get; set; }
    [JsonPropertyName("page_count_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PageCountAfter { get; set; }
    [JsonPropertyName("freelist_count_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FreelistCountAfter { get; set; }
    [JsonPropertyName("freelist_ratio_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FreelistRatioAfter { get; set; }
    [JsonPropertyName("pages_reclaimed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PagesReclaimed { get; set; }
    [JsonPropertyName("bytes_reclaimed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BytesReclaimed { get; set; }
    [JsonPropertyName("logical_database_bytes_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LogicalDatabaseBytesBefore { get; set; }
    [JsonPropertyName("logical_database_bytes_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LogicalDatabaseBytesAfter { get; set; }
    [JsonPropertyName("db_size_bytes_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DbSizeBytesBefore { get; set; }
    [JsonPropertyName("db_size_bytes_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DbSizeBytesAfter { get; set; }
    [JsonPropertyName("auto_vacuum_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AutoVacuumMode { get; set; }
}

public sealed class StatusFailedOrPartialIndexRun
{
    public string? Status { get; set; }
    public string? Mode { get; set; }
    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }
    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; set; }
    [JsonPropertyName("files_processed")]
    public long? FilesProcessed { get; set; }
    [JsonPropertyName("files_total")]
    public long? FilesTotal { get; set; }
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }
    public string? Reason { get; set; }
    [JsonPropertyName("progress_persisted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProgressPersisted { get; set; }
    [JsonPropertyName("recovery_hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecoveryHint { get; set; }
    [JsonPropertyName("file_errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StatusIndexFileError>? FileErrors { get; set; }
}

public sealed class StatusIndexFileError
{
    public string File { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Line { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Column { get; set; }
}

[JsonSerializable(typeof(List<StatusIndexFileError>))]
[JsonSerializable(typeof(ReferenceExtractionCapHitSummary))]
[JsonSerializable(typeof(StatusRebuildReclaim))]
internal sealed partial class StatusMetadataJsonContext : JsonSerializerContext
{
}

public sealed class StatusRepairCommand
{
    public string Name { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public List<string> Args { get; set; } = [];
    public string Reason { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = [];
    [JsonPropertyName("mutation_class")]
    public string MutationClass { get; set; } = string.Empty;
    [JsonPropertyName("safety_class")]
    public string SafetyClass { get; set; } = string.Empty;
    [JsonPropertyName("safety_notes")]
    public List<string> SafetyNotes { get; set; } = [];
}

public sealed class StatusUnknownExtensionGroup
{
    public string Extension { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("recommended_action")]
    public string RecommendedAction { get; set; } = string.Empty;
    public long Count { get; set; }
    [JsonPropertyName("sample_paths")]
    public List<string> SamplePaths { get; set; } = [];
    [JsonPropertyName("sample_paths_truncated")]
    public bool SamplePathsTruncated { get; set; }
}

public class StatusReadinessDegradation
{
    public string Field { get; set; } = string.Empty;
    [JsonPropertyName("root_cause")]
    public string RootCause { get; set; } = string.Empty;
    [JsonPropertyName("degraded_reason")]
    public string DegradedReason { get; set; } = string.Empty;
    [JsonPropertyName("recommended_action")]
    public string RecommendedAction { get; set; } = string.Empty;
    [JsonPropertyName("alternative_action")]
    public string AlternativeAction { get; set; } = string.Empty;
}

public class StatusDbPragmaSettings
{
    public string? JournalMode { get; set; }
    public string? Synchronous { get; set; }
    public long? WalAutocheckpoint { get; set; }
    [JsonPropertyName("busy_timeout_ms")]
    public long? BusyTimeoutMs { get; set; }
    public long? PageCount { get; set; }
    public long? FreelistCount { get; set; }
    public long? PageSize { get; set; }
    public long? AutoVacuum { get; set; }
}

public sealed record VacuumResult(
    string Status,
    bool DryRun,
    long PageSize,
    long PageCountBefore,
    long FreelistCountBefore,
    long PageCountAfter,
    long FreelistCountAfter,
    long PagesReclaimed,
    long BytesReclaimed,
    long EstimatedPagesReclaimable,
    long EstimatedBytesReclaimable,
    long? DbSizeBytesBefore,
    long? WalSizeBytesBefore,
    long? DbSizeBytesAfter,
    long? WalSizeBytesAfter,
    long LogicalDatabaseBytesBefore,
    long LogicalDatabaseBytesAfter,
    long? MainFileBytesBefore,
    long? MainFileBytesAfter,
    long? WalFileBytesBefore,
    long? WalFileBytesAfter,
    long? ShmFileBytesBefore,
    long? ShmFileBytesAfter,
    long? PhysicalFileSetBytesBefore,
    long? PhysicalFileSetBytesAfter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WalCheckpointTimingNote,
    long AutoVacuumModeBefore,
    string AutoVacuumModeBeforeName,
    long AutoVacuumModeAfter,
    string AutoVacuumModeAfterName,
    StatusMaintenanceGuidance MaintenanceGuidance,
    [property: JsonPropertyName("api_version")] string ApiVersion = JsonOutputContract.ApiVersion);

public class PostExtractionHookStatus
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    [JsonPropertyName("callback_budget_ms")]
    public long CallbackBudgetMs { get; set; }
    [JsonPropertyName("load_context_lifecycle")]
    public string LoadContextLifecycle { get; set; } = PostExtractionHookRunner.HookLoadContextLifecycle;
}

public class RepoMapResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public int FileCount { get; set; }
    public long TotalLines { get; set; }
    public long TotalSymbols { get; set; }
    public long TotalReferences { get; set; }
    public DateTime? IndexedAt { get; set; }
    public DateTime? LatestModified { get; set; }
    public DateTime? WorkspaceIndexedAt { get; set; }
    public DateTime? WorkspaceLatestModified { get; set; }
    public string? ProjectRoot { get; set; }
    public string? GitHead { get; set; }
    public bool? GitIsDirty { get; set; }
    [JsonPropertyName("indexed_head_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadCommit { get; set; }
    [JsonPropertyName("indexed_head_sha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadSha { get; set; }
    [JsonPropertyName("workspace_verified_head_sha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceVerifiedHeadSha { get; set; }
    [JsonPropertyName("indexed_head_branch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadBranch { get; set; }
    [JsonPropertyName("indexed_head_timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? IndexedHeadTimestamp { get; set; }
    [JsonPropertyName("commits_ahead_of_indexed_head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CommitsAheadOfIndexedHead { get; set; }
    [JsonPropertyName("worktree_head_changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorktreeHeadChanged { get; set; }
    [JsonPropertyName("head_freshness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusHeadFreshness? HeadFreshness => StatusHeadFreshness.FromMap(this);
    [JsonIgnore]
    internal RepoMapIndexedHeadSnapshot? IndexedHeadSnapshot { get; set; }
    [JsonIgnore]
    internal int IssueDraftCandidateCount { get; set; }
    [JsonIgnore]
    internal List<RepoFileSummaryResult> IssueDraftCandidates { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LanguageCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ModuleCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EntrypointCount { get; set; }
    public List<RepoLanguageResult> Languages { get; set; } = [];
    public List<RepoModuleResult> Modules { get; set; } = [];
    public List<RepoFileSummaryResult> TopFiles { get; set; } = [];
    public List<RepoFileSummaryResult> LargestFiles { get; set; } = [];
    public List<RepoFileSummaryResult> SymbolRichFiles { get; set; } = [];
    public List<RepoFileSummaryResult> ReferenceRichFiles { get; set; } = [];
    public List<RepoEntrypointResult> Entrypoints { get; set; } = [];
    /// <summary>
    /// False when reference-derived aggregates (TotalReferences, per-file / per-module /
    /// per-language reference counts, ReferenceRichFiles) were synthesized as 0 because the
    /// graph table was missing (legacy / read-only DB). Callers must not rank or prioritize
    /// based on reference counts when this is false — the repo may actually be reference-rich.
    /// 参照系集計が欠損テーブルによりゼロ合成されている場合 false。ランキングに使わないこと。
    /// </summary>
    public bool GraphTableAvailable { get; set; } = true;
}

internal sealed record RepoMapIndexedHeadSnapshot(
    string? ProjectRoot,
    string? LegacyFullScanHead,
    string? WorkspaceVerifiedHead,
    string? LatestIndexHead,
    string? LatestIndexBranch,
    DateTimeOffset? LatestIndexTimestamp,
    bool LatestIndexBranchStampPresent,
    string? LegacyFullScanBranch,
    bool LegacyFullScanBranchStampPresent);

public class RepoLanguageResult
{
    public string Lang { get; set; } = string.Empty;
    public int Files { get; set; }
    public long Lines { get; set; }
    public long Symbols { get; set; }
    public long References { get; set; }
}

public class RepoModuleResult
{
    public string Module { get; set; } = string.Empty;
    public int Files { get; set; }
    public long Lines { get; set; }
    public long Symbols { get; set; }
    public long References { get; set; }
}

public class RepoFileSummaryResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int Lines { get; set; }
    public long Size { get; set; }
    public int SymbolCount { get; set; }
    public int ReferenceCount { get; set; }
    public long? Score { get; set; }
}

public class RepoEntrypointResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Score { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public int HintRank { get; set; }
}

public class SymbolAnalysisResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Query { get; set; } = string.Empty;
    public FileResult? File { get; set; }
    public DateTime? WorkspaceIndexedAt { get; set; }
    public DateTime? WorkspaceLatestModified { get; set; }
    public string? ProjectRoot { get; set; }
    public string? GitHead { get; set; }
    public bool? GitIsDirty { get; set; }
    [JsonPropertyName("indexed_head_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadCommit { get; set; }
    [JsonPropertyName("workspace_verified_head_sha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceVerifiedHeadSha { get; set; }
    [JsonPropertyName("indexed_head_sha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadSha { get; set; }
    // AnalyzeSymbol captures persisted provenance in the same SQLite transaction as the
    // analysis body. Runtime Git enrichment consumes these ignored fields without reopening
    // the database or mixing index generations. Issue #5054.
    // AnalyzeSymbol の本文と同じ SQLite transaction で persisted provenance を取得し、
    // runtime Git 補強では DB を再読込せず index 世代を混在させない。
    [JsonIgnore]
    internal bool HeadMetadataSnapshotCaptured { get; set; }
    [JsonIgnore]
    internal string? IndexedHeadCommitBranchSnapshot { get; set; }
    [JsonIgnore]
    internal bool IndexedHeadCommitBranchStampPresentSnapshot { get; set; }
    [JsonIgnore]
    internal string? IndexedHeadBranchSnapshot { get; set; }
    [JsonIgnore]
    internal bool IndexedHeadBranchStampPresentSnapshot { get; set; }
    [JsonPropertyName("worktree_head_changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorktreeHeadChanged { get; set; }
    public string? GraphLanguage { get; set; }
    /// <summary>
    /// Provenance for <see cref="GraphLanguage"/>: `language_filter`, `definition`, or
    /// `graph_evidence`.
    /// <see cref="GraphLanguage"/> の由来: `language_filter`、`definition`、`graph_evidence`。
    /// </summary>
    public string? GraphLanguageSource { get; set; }
    /// <summary>
    /// Confidence of the graph-language decision: `authoritative`, `inferred_consistent`,
    /// or `conflicted`.
    /// graph language 判定の信頼度: `authoritative`、`inferred_consistent`、`conflicted`。
    /// </summary>
    public string? GraphLanguageConfidence { get; set; }
    /// <summary>
    /// Distinct graph-result languages considered when no filter or definition supplied
    /// an authoritative language.
    /// filter / definition から authoritative な言語が得られない場合に検討した graph result 言語。
    /// </summary>
    public List<string> GraphLanguageCandidates { get; set; } = [];
    /// <summary>
    /// True when definition-free graph evidence contains more than one language.
    /// definition が無い graph evidence に複数言語が含まれる場合は true。
    /// </summary>
    public bool GraphLanguageConflict { get; set; }
    public bool? GraphSupported { get; set; }
    public string? GraphSupportReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? GraphDegraded { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnsupportedSymbolKind { get; set; }
    public List<DefinitionResult> Definitions { get; set; } = [];
    public List<SymbolResult> NearbySymbols { get; set; } = [];
    public List<ReferenceResult> References { get; set; } = [];
    public List<CallerResult> Callers { get; set; } = [];
    public List<CalleeResult> Callees { get; set; } = [];
    [JsonPropertyName("graph_sections")]
    public SymbolGraphSections GraphSections { get; set; } = new();
    [JsonPropertyName("candidate_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CandidateCount { get; set; }
    [JsonPropertyName("graph_scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GraphScope { get; set; }
    [JsonPropertyName("selection_required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SelectionRequired { get; set; }
    [JsonPropertyName("candidate_bundles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SymbolCandidateBundle>? CandidateBundles { get; set; }
    /// <summary>
    /// False when the index does not contain the reference table (legacy / read-only DB),
    /// meaning empty References / Callers / Callees are degraded — not a true "no callers".
    /// インデックスに参照テーブルが無いと true / false で区別可能。空が本物かどうか見極める。
    /// </summary>
    public bool GraphTableAvailable { get; set; } = true;
    /// <summary>
    /// True when bundled SQL graph-backed reads in this analysis reflect the current
    /// call-column / qualified-name contract.
    /// bundle 内の SQL graph 読み取りが current 契約に揃っているかどうか。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SqlGraphContractReady { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlGraphContractDegradedReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExactZeroHintResult? ExactZeroHint { get; set; }
    /// <summary>
    /// True when every active `--exact` sub-query in the bundle can use its supporting indexes.
    /// False means the bundled result still returns correct hits, but at least one exact
    /// sub-query degraded to a slower legacy fallback path.
    /// bundle 内の `--exact` sub-query がすべて対応 index を使えるか。false でも結果は正しい。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExactIndexAvailable { get; set; }
    [JsonIgnore]
    public bool? ExactHasMissingIndex { get; set; }
    [JsonIgnore]
    public bool? ExactHasMissingTable { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DegradedReason { get; set; }
}

public class SymbolCandidateSelector
{
    [JsonPropertyName("selector")]
    public string Selector { get; set; } = string.Empty;
    [JsonPropertyName("symbol_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SymbolId { get; set; }
    [JsonPropertyName("qualified_name")]
    public string QualifiedName { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Container { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }
    public string Path { get; set; } = string.Empty;
    public int Line { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lang { get; set; }
    public string Kind { get; set; } = string.Empty;
}

public class SymbolCandidateBundle
{
    public SymbolCandidateSelector Selector { get; set; } = new();
    public DefinitionResult Definition { get; set; } = new();
    public FileResult? File { get; set; }
    public bool? GraphSupported { get; set; }
    public string? GraphSupportReason { get; set; }
    [JsonPropertyName("identity_scoped")]
    public bool IdentityScoped { get; set; }
    public List<SymbolResult> NearbySymbols { get; set; } = [];
    public List<ReferenceResult> References { get; set; } = [];
    public List<CallerResult> Callers { get; set; } = [];
    public List<CalleeResult> Callees { get; set; } = [];
    [JsonPropertyName("graph_sections")]
    public SymbolGraphSections GraphSections { get; set; } = new();
}

/// <summary>
/// Independent completeness metadata for the bounded graph sections in an inspect bundle.
/// inspect bundle 内で個別に上限適用される graph section の完全性メタデータ。
/// </summary>
public class SymbolGraphSections
{
    public SymbolGraphSection References { get; set; } = new();
    public SymbolGraphSection Callers { get; set; } = new();
    public SymbolGraphSection Callees { get; set; } = new();
}

public class SymbolGraphSection
{
    public int Total { get; set; }
    public int Returned { get; set; }
    public int Offset { get; set; }
    public bool Truncated { get; set; }
    [JsonPropertyName("next_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; set; }
}

public sealed record SymbolGraphPageRequest(
    string Section,
    int Offset,
    string? CandidateSelector);

/// <summary>
/// Structured symbol outline for a single file.
/// 1ファイルの構造化シンボルアウトライン。
/// </summary>
public class OutlineResult
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; set; } = JsonOutputContract.ApiVersion;
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int TotalLines { get; set; }
    public int SymbolCount { get; set; }
    public List<OutlineSymbol> Symbols { get; set; } = [];
}

public class OutlineSymbol
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Line { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int Depth { get; set; }
    public int? BodyStartLine { get; set; }
    public int? BodyEndLine { get; set; }
    public string? Signature { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SignatureTruncated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SignatureOriginalLength { get; set; }
    public string? ContainerKind { get; set; }
    public string? ContainerName { get; set; }
    public string? Visibility { get; set; }
    public string? ReturnType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SortMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SizeLines { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ComplexityScore { get; set; }
}

internal sealed class RepoFileStat
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? ModuleName { get; set; }
    public long Size { get; set; }
    public int Lines { get; set; }
    public int SymbolCount { get; set; }
    public int ReferenceCount { get; set; }
    public string? Checksum { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? IndexedAt { get; set; }
}

public class FileDependencyResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultKind { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceDb { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetDb { get; set; }
    public int ReferenceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RankingScore { get; set; }
    public string Symbols { get; set; } = string.Empty;
    [JsonIgnore]
    public List<string>? SymbolSamples { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FileDependencyEvidence>? Evidence { get; set; }
}

public class FileDependencyEvidence
{
    public string SourceLanguage { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string ReferenceKind { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public int ReferenceCount { get; set; }
}
