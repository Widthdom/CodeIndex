namespace CodeIndex.Database;

public sealed record DegradationReasonMetadata(
    string Code,
    string HumanText,
    string RecommendedAction,
    string AlternativeAction);

public static class DegradationReasonCodes
{
    public const string MissingFoldBackfill = "missing_fold_backfill";
    public const string StaleFoldKeyVersion = "stale_fold_key_version";
    public const string StaleFoldKeyFingerprint = "stale_fold_key_fingerprint";
    public const string FoldRowsNotRestamped = "fold_rows_not_restamped";
    public const string FoldReadyNotReady = "fold_ready=false";
    public const string SqlGraphContractNotReady = "sql_graph_contract_ready=false";
    public const string HotspotFamilyNotReady = "hotspot_family_ready=false";
    public const string GraphTableMissing = "graph_table_available=false";
    public const string IssuesTableMissing = "issues_table_available=false";
    public const string CSharpSymbolNameNotReady = "csharp_symbol_name_ready=false";
    public const string CSharpMetadataTargetNotReady = "csharp_metadata_target_ready=false";
    public const string IndexNewerThanReader = "index_newer_than_reader=true";

    public static readonly IReadOnlyList<string> All =
    [
        MissingFoldBackfill,
        StaleFoldKeyVersion,
        StaleFoldKeyFingerprint,
        FoldRowsNotRestamped,
        FoldReadyNotReady,
        SqlGraphContractNotReady,
        HotspotFamilyNotReady,
        GraphTableMissing,
        IssuesTableMissing,
        CSharpSymbolNameNotReady,
        CSharpMetadataTargetNotReady,
        IndexNewerThanReader,
    ];

    private static readonly IReadOnlyDictionary<string, DegradationReasonMetadata> MetadataByCode =
        All.ToDictionary(code => code, CreateMetadata, StringComparer.Ordinal);

    public static DegradationReasonMetadata GetMetadata(string code)
        => MetadataByCode.TryGetValue(code, out var metadata)
            ? metadata
            : new DegradationReasonMetadata(
                code,
                "Index readiness is degraded for an unrecognized reason.",
                "Run `cdidx status --json` to inspect the current DB state.",
                "Run `cdidx index <projectPath>` to rebuild the index.");

    public static string BuildFoldNotReadyExplanation(string? foldReadyReason)
        => GetMetadata(NormalizeFoldReason(foldReadyReason)).HumanText;

    public static string BuildSqlGraphContractDegradedReason()
        => $"{SqlGraphContractNotReady} ({GetMetadata(SqlGraphContractNotReady).HumanText})";

    public static string BuildHotspotFamilyLanguageDegradedReason(string language)
        => $"cross-file hotspot family grouping for '{language}' is degraded; run `cdidx index <projectPath>` to restamp authoritative hotspot families.";

    public static string BuildHotspotFamilyLanguagesDegradedReason(IEnumerable<string> languages)
        => $"cross-file hotspot family grouping is degraded for: {string.Join(", ", languages)}; run `cdidx index <projectPath>` to restamp authoritative hotspot families.";

    public static string NormalizeFoldReason(string? foldReadyReason)
        => foldReadyReason switch
        {
            MissingFoldBackfill => MissingFoldBackfill,
            StaleFoldKeyVersion => StaleFoldKeyVersion,
            StaleFoldKeyFingerprint => StaleFoldKeyFingerprint,
            FoldRowsNotRestamped => FoldRowsNotRestamped,
            _ => FoldRowsNotRestamped
        };

    private static DegradationReasonMetadata CreateMetadata(string code)
        => code switch
        {
            MissingFoldBackfill => new(
                code,
                "--exact falls back to ASCII COLLATE NOCASE because legacy rows without `name_folded` remain.",
                "Run `cdidx backfill-fold` to restamp folded-name columns in place.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            StaleFoldKeyVersion => new(
                code,
                "--exact falls back to ASCII COLLATE NOCASE because unchanged rows still carry an older fold-key version.",
                "Run `cdidx backfill-fold` to restamp folded-name columns in place.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            StaleFoldKeyFingerprint => new(
                code,
                "--exact falls back to ASCII COLLATE NOCASE because unchanged rows still carry folded keys generated under an older runtime fingerprint.",
                "Run `cdidx backfill-fold` to restamp folded-name columns in place.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            FoldRowsNotRestamped => new(
                code,
                "--exact falls back to ASCII COLLATE NOCASE because some folded-name rows were not restamped under the current runtime.",
                "Run `cdidx backfill-fold` to restamp folded-name columns in place.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            FoldReadyNotReady => new(
                code,
                "Unicode exact-name fold readiness is degraded.",
                "Run `cdidx backfill-fold` to restamp folded-name columns in place.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            SqlGraphContractNotReady => new(
                code,
                "SQL graph rows may still use a stale call-column / qualified-name contract; rerun `cdidx index <projectPath>` before trusting SQL graph/dependency results.",
                "Run `cdidx index <projectPath>` to rewrite SQL graph rows.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            HotspotFamilyNotReady => new(
                code,
                "Cross-file hotspot grouping may be degraded for one or more languages.",
                "Run `cdidx index <projectPath>` to restamp authoritative hotspot families.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            GraphTableMissing => new(
                code,
                "Reference / caller / callee / unused counts are degraded to 0 because the symbol_references table is missing.",
                "Run `cdidx index <projectPath>` to rebuild the graph-capable index.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            IssuesTableMissing => new(
                code,
                "Validate output is degraded to empty because the file_issues table is missing.",
                "Run `cdidx index <projectPath>` to rebuild the issue table.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            CSharpSymbolNameNotReady => new(
                code,
                "C# exact-name for operators / conversion operators / indexers is degraded.",
                "Run `cdidx index <projectPath>` to upgrade canonical C# symbol names.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            CSharpMetadataTargetNotReady => new(
                code,
                "C# metadata attribute dependency edges are using legacy heuristics.",
                "Run `cdidx index <projectPath>` to restamp authoritative C# metadata targets.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            IndexNewerThanReader => new(
                code,
                "This DB was written by a newer cdidx, so older readers may degrade instead of trusting newer contract stamps.",
                "Run status with a current cdidx binary.",
                "Rebuild the DB with the cdidx version you intend to use."),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown degradation reason code.")
        };
}
