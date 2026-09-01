using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed class IndexDatabaseSnapshot
    {
        public string? FoldVersion { get; init; }
        public string? FoldFingerprint { get; init; }
        public string? CSharpSymbolNameContractVersion { get; init; }
        public bool? CSharpStaticInterfaceSourceEvidence { get; set; }
        public string? MetadataTargetCSharp { get; init; }
        public string? SqlGraphContractVersion { get; init; }
        public string? HdlGraphContractVersion { get; init; }
        public bool SymbolsOnlyGraphOmitted { get; init; }
        public bool IndexComplete { get; init; }
        public int Readiness { get; init; }
        public required Dictionary<string, string?> HotspotFamilyVersions { get; init; }
        public required Dictionary<string, string?> HotspotFamilyMarkerFingerprints { get; init; }
        public string? IndexedProjectRoot { get; init; }
        public string? SymbolKindFilterSignature { get; init; }
        public bool SymbolKindFilterAuditCurrent { get; init; }
    }

    private static IndexDatabaseSnapshot CaptureIndexDatabaseSnapshot(DbContext db)
    {
        var readiness = db.GetUserVersion();
        var csharpMetadataTargetVersionMetaKey = DbContext.GetMetadataTargetVersionMetaKey("csharp");
        var meta = db.GetMetaStrings(
        [
            "fold_key_version",
            "fold_key_fingerprint",
            DbContext.CSharpSymbolNameContractVersionMetaKey,
            DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey,
            csharpMetadataTargetVersionMetaKey,
            DbContext.SqlGraphContractVersionMetaKey,
            DbContext.HdlGraphContractVersionMetaKey,
            DbContext.SymbolsOnlyGraphOmittedMetaKey,
            DbContext.IndexCompletenessMetaKey,
            DbContext.IndexedProjectRootMetaKey,
            IndexCommandRunner.SymbolKindFilterMetaKey,
            IndexCommandRunner.SymbolKindFilterAuditVersionMetaKey,
        ]);

        return new IndexDatabaseSnapshot
        {
            FoldVersion = meta["fold_key_version"],
            FoldFingerprint = meta["fold_key_fingerprint"],
            CSharpSymbolNameContractVersion = meta[DbContext.CSharpSymbolNameContractVersionMetaKey],
            CSharpStaticInterfaceSourceEvidence =
                bool.TryParse(
                    meta[DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey],
                    out var parsedCSharpStaticInterfaceSourceEvidence)
                    ? parsedCSharpStaticInterfaceSourceEvidence
                    : null,
            MetadataTargetCSharp = meta[csharpMetadataTargetVersionMetaKey],
            SqlGraphContractVersion = meta[DbContext.SqlGraphContractVersionMetaKey],
            HdlGraphContractVersion = meta[DbContext.HdlGraphContractVersionMetaKey],
            SymbolsOnlyGraphOmitted = string.Equals(
                meta[DbContext.SymbolsOnlyGraphOmittedMetaKey],
                "true",
                StringComparison.OrdinalIgnoreCase),
            IndexComplete = string.Equals(
                meta[DbContext.IndexCompletenessMetaKey],
                "complete",
                StringComparison.OrdinalIgnoreCase),
            Readiness = readiness,
            HotspotFamilyVersions = GetHotspotFamilyMetaSnapshot(
                db,
                DbContext.GetHotspotFamilyVersionMetaKey),
            HotspotFamilyMarkerFingerprints = GetHotspotFamilyMetaSnapshot(
                db,
                DbContext.GetHotspotFamilyMarkerFingerprintMetaKey),
            IndexedProjectRoot = meta[DbContext.IndexedProjectRootMetaKey],
            SymbolKindFilterSignature = meta[IndexCommandRunner.SymbolKindFilterMetaKey],
            SymbolKindFilterAuditCurrent = string.Equals(
                    meta[IndexCommandRunner.SymbolKindFilterAuditVersionMetaKey],
                    DbContext.SymbolKindFilterAuditVersion,
                    StringComparison.Ordinal)
                && (readiness & DbContext.SymbolKindFilterAuditStorageContractFlag) != 0,
        };
    }
}
