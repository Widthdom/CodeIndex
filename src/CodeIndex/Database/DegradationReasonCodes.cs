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
    public const string FoldReadyBitSetButRowsIncomplete = "fold_ready_bit_set_but_rows_incomplete";
    public const string FoldReadyNotReady = "fold_ready=false";
    public const string SqlGraphContractNotReady = "sql_graph_contract_ready=false";
    public const string HotspotFamilyNotReady = "hotspot_family_ready=false";
    public const string HotspotFamilySupportNotIndexed = "hotspot_family_support_not_indexed";
    public const string HotspotFamilyMetadataStale = "hotspot_family_metadata_stale";
    public const string HotspotFamilyDisabledAtIndexTime = "hotspot_family_disabled_at_index_time";
    public const string HotspotFamilyMarkerFingerprintIncomplete = "hotspot_family_marker_fingerprint_incomplete";
    public const string HotspotFamilyRowsIncomplete = "partial_family_key_population";
    public const string GraphTableMissing = "graph_table_available=false";
    public const string IssuesTableMissing = "issues_table_available=false";
    public const string FileIssuesDataStale = "file_issues_data_current=false";
    public const string CSharpSymbolNameNotReady = "csharp_symbol_name_ready=false";
    public const string CSharpMetadataTargetNotReady = "csharp_metadata_target_ready=false";
    public const string CSharpMetadataTargetMissingColumn = "csharp_metadata_target_missing_column";
    public const string CSharpMetadataTargetStampOutdated = "csharp_metadata_target_stamp_outdated";
    public const string IndexNewerThanReader = "index_newer_than_reader=true";
    public const string MigrationInProgress = "migration_in_progress";

    public static readonly IReadOnlyList<string> All =
    [
        MissingFoldBackfill,
        StaleFoldKeyVersion,
        StaleFoldKeyFingerprint,
        FoldRowsNotRestamped,
        FoldReadyBitSetButRowsIncomplete,
        FoldReadyNotReady,
        SqlGraphContractNotReady,
        HotspotFamilyNotReady,
        HotspotFamilySupportNotIndexed,
        HotspotFamilyMetadataStale,
        HotspotFamilyDisabledAtIndexTime,
        HotspotFamilyMarkerFingerprintIncomplete,
        HotspotFamilyRowsIncomplete,
        GraphTableMissing,
        IssuesTableMissing,
        FileIssuesDataStale,
        CSharpSymbolNameNotReady,
        CSharpMetadataTargetNotReady,
        CSharpMetadataTargetMissingColumn,
        CSharpMetadataTargetStampOutdated,
        IndexNewerThanReader,
        MigrationInProgress,
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
        => $"cross-file hotspot family grouping for '{language}' is degraded; run `cdidx index <projectPath> --rebuild` to restamp authoritative hotspot families for every indexed row.";

    public static string BuildHotspotFamilyLanguagesDegradedReason(IEnumerable<string> languages)
        => $"cross-file hotspot family grouping is degraded for: {string.Join(", ", languages)}; run `cdidx index <projectPath> --rebuild` to restamp authoritative hotspot families for every indexed row.";

    public static string NormalizeFoldReason(string? foldReadyReason)
        => foldReadyReason switch
        {
            MissingFoldBackfill => MissingFoldBackfill,
            StaleFoldKeyVersion => StaleFoldKeyVersion,
            StaleFoldKeyFingerprint => StaleFoldKeyFingerprint,
            FoldRowsNotRestamped => FoldRowsNotRestamped,
            FoldReadyBitSetButRowsIncomplete => FoldReadyBitSetButRowsIncomplete,
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
            FoldReadyBitSetButRowsIncomplete => new(
                code,
                "--exact falls back to ASCII COLLATE NOCASE because the fold-ready bit is set but row-level folded-name verification found incomplete rows.",
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
                "Run `cdidx index <projectPath> --rebuild` to restamp authoritative hotspot families for every indexed row.",
                "Run `cdidx index <projectPath> --files <changedFiles>` only when you can enumerate every file whose hotspot-family rows need restamping."),
            HotspotFamilySupportNotIndexed => new(
                code,
                "Cross-file hotspot grouping is unavailable because this DB predates hotspot-family metadata or lacks the required symbol columns.",
                "Run `cdidx index <projectPath> --rebuild` to rebuild and stamp authoritative hotspot families for every indexed row.",
                "Create a fresh DB with the current cdidx binary if the existing DB cannot be rebuilt in place."),
            HotspotFamilyMetadataStale => new(
                code,
                "Cross-file hotspot grouping metadata was written by an older hotspot-family contract.",
                "Run `cdidx index <projectPath> --rebuild` to restamp authoritative hotspot families for every indexed row.",
                "Run `cdidx index <projectPath> --files <changedFiles>` only when you can enumerate every stale file."),
            HotspotFamilyDisabledAtIndexTime => new(
                code,
                "Cross-file hotspot grouping metadata was stamped without marker fingerprints, so authoritative family grouping cannot be trusted.",
                "Run `cdidx index <projectPath> --rebuild` to rebuild and stamp authoritative hotspot families for every indexed row.",
                "Run `cdidx index <projectPath> --files <changedFiles>` only when you can enumerate every affected file."),
            HotspotFamilyMarkerFingerprintIncomplete => new(
                code,
                "Cross-file hotspot grouping metadata is degraded because project-marker fingerprint traversal hit safety limits.",
                "Exclude generated or irrelevant project-marker trees with `.gitignore` or `.cdidxignore`, then run `cdidx index <projectPath> --rebuild`.",
                "Run `cdidx status --json` to monitor degraded readiness until the project-marker tree can be bounded."),
            HotspotFamilyRowsIncomplete => new(
                code,
                "Cross-file hotspot grouping metadata is stamped, but some indexed symbols still lack family keys.",
                "Run `cdidx index <projectPath> --rebuild` to restamp authoritative hotspot families for every indexed row.",
                "Run `cdidx index <projectPath> --files <changedFiles>` only when you can enumerate every file with incomplete family keys."),
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
            FileIssuesDataStale => new(
                code,
                "The file_issues table exists, but its data is not stamped current for this index generation.",
                "Run `cdidx index <projectPath>` to refresh file issue rows.",
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
            CSharpMetadataTargetMissingColumn => new(
                code,
                "C# metadata attribute dependency edges are degraded because the symbols.is_metadata_target column is missing.",
                "Run `cdidx index <projectPath> --rebuild` to recreate the index with metadata-target storage.",
                "Run `cdidx index <projectPath>` after upgrading if the DB can be migrated in place."),
            CSharpMetadataTargetStampOutdated => new(
                code,
                "C# metadata attribute dependency edges are degraded because the metadata-target version stamp is missing or stale.",
                "Run `cdidx index <projectPath>` to restamp authoritative C# metadata targets.",
                "Run `cdidx index <projectPath> --rebuild` for a full rebuild."),
            IndexNewerThanReader => new(
                code,
                "This DB was written by a newer cdidx, so older readers may degrade instead of trusting newer contract stamps.",
                "Run status with a current cdidx binary.",
                "Rebuild the DB with the cdidx version you intend to use."),
            MigrationInProgress => new(
                code,
                "An index write or migration is currently in progress; readiness may be temporarily degraded until the writer finishes.",
                "Wait for the active `cdidx index` run to finish, then rerun `cdidx status --json`.",
                "If no index process is running, run `cdidx index <projectPath> --rebuild` to recover from the interrupted batch."),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown degradation reason code.")
        };
}
