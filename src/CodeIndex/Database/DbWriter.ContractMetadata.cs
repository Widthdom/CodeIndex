using CodeIndex.Indexer;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private bool _referenceIdentityContractKnownInvalid;

    public void StampSymbolExtractorVersions(IReadOnlyCollection<string>? languagesToStamp = null)
    {
        var languages = languagesToStamp ?? GetIndexedLanguages();
        var values = new List<(string Key, string? Value)>(languages.Count);
        foreach (var lang in languages)
        {
            if (string.IsNullOrWhiteSpace(lang))
                continue;

            values.Add((
                DbContext.GetSymbolExtractorVersionMetaKey(lang),
                SymbolExtractor.GetContractVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        SetMetaValues(values.ToArray());
    }

    /// <summary>
    /// Stamp reference-graph contracts only for languages whose graph rows were regenerated.
    /// This is intentionally separate from symbol-extractor versions because fold-only
    /// maintenance may restamp symbol metadata without reparsing references.
    /// reference graph を再生成した言語だけに専用 contract を stamp する。fold-only
    /// maintenance が symbol metadata を更新しても、旧 graph を current と誤認しない。
    /// </summary>
    public void StampDynamicReferenceGraphContracts(IReadOnlyCollection<string> languagesToStamp)
    {
        var values = new List<(string Key, string? Value)>(languagesToStamp.Count);
        foreach (var lang in languagesToStamp)
        {
            if (!SymbolExtractor.RequiresExplicitReferenceGraphContractStamp(lang))
                continue;

            values.Add((
                DbContext.GetDynamicReferenceGraphContractVersionMetaKey(lang),
                SymbolExtractor.GetReferenceGraphContractVersion(lang).ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));
        }

        SetMetaValues(values.ToArray());
    }

    /// <summary>
    /// Stamp the current C# symbol-name contract version. Readers and indexers use this to
    /// detect canonical-name upgrades such as operator/conversion/indexer renames.
    /// C# canonical symbol name 契約の current version を stamp する。
    /// </summary>
    public void MarkCSharpSymbolNameContractReady()
    {
        SetMeta(
            DbContext.CSharpSymbolNameContractVersionMetaKey,
            DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Stamp the current SQL graph storage contract version. Readers use this to distinguish
    /// pre-fix SQL graph rows (stale call columns / symbol names) from rows rewritten by the
    /// current extractor/name-resolution contract.
    /// SQL graph 保存契約の current version を stamp する。
    /// </summary>
    public void MarkSqlGraphContractReady()
    {
        SetMeta(
            DbContext.SqlGraphContractVersionMetaKey,
            DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Stamp the HDL graph extraction contract after every indexed Verilog, SystemVerilog,
    /// and VHDL file has been refreshed by a successful full scan.
    /// HDL graph 抽出契約を、対象ファイルの full-scan 更新完了後に stamp する。
    /// </summary>
    public void MarkHdlGraphContractReady()
    {
        SetMeta(
            DbContext.HdlGraphContractVersionMetaKey,
            DbContext.HdlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void MarkIndexReaderContractsReady(bool symbolsOnlyGraphOmitted)
    {
        var csharpVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (symbolsOnlyGraphOmitted)
        {
            SetMetaValues(
                (DbContext.CSharpSymbolNameContractVersionMetaKey, csharpVersion),
                (DbContext.SymbolsOnlyGraphOmittedMetaKey, "true"));
            return;
        }

        SetMetaValues(
            (DbContext.CSharpSymbolNameContractVersionMetaKey, csharpVersion),
            (DbContext.SqlGraphContractVersionMetaKey, sqlVersion),
            (DbContext.SymbolsOnlyGraphOmittedMetaKey, null));
    }

    public void ClearSqlGraphContractReady()
    {
        SetMeta(DbContext.SqlGraphContractVersionMetaKey, null);
    }

    public bool ReferenceIdentityContractMatchesCurrent()
        => string.Equals(
            GetMetaString(DbContext.ReferenceIdentityContractVersionMetaKey),
            DbContext.ReferenceIdentityContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    public void MarkReferenceIdentityContractReady()
    {
        SetMeta(
            DbContext.ReferenceIdentityContractVersionMetaKey,
            DbContext.ReferenceIdentityContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _referenceIdentityContractKnownInvalid = false;
    }

    public void ClearReferenceIdentityContractReady()
    {
        SetMeta(DbContext.ReferenceIdentityContractVersionMetaKey, null);
        _referenceIdentityContractKnownInvalid = true;
    }

    private void InvalidateReferenceIdentityContractForMutation()
    {
        if (_referenceIdentityContractKnownInvalid)
            return;

        ClearReferenceIdentityContractReady();
        if (IsInTransaction())
        {
            // A surrounding transaction may still roll back the marker deletion.
            // 外側 transaction が marker 削除を rollback する可能性があるため cache しない。
            _referenceIdentityContractKnownInvalid = false;
        }
    }

    /// <summary>
    /// Stamp the current authoritative version for hotspot family grouping semantics.
    /// Only fully authoritative DB states should call this; mixed legacy/current DBs must
    /// stay unstamped so readers degrade to conservative same-file counting.
    /// hotspots family grouping の current authoritative version を stamp する。
    /// </summary>
    public void MarkHotspotFamilyReady(string lang, string? markerFingerprint = null)
    {
        // Clear the superseded global keys so mixed-version DBs don't leave confusing stale metadata behind.
        // 廃止した global key を掃除し、混在 DB に紛らわしい古い metadata を残さない。
        SetMetaValues(
            (DbContext.GetHotspotFamilyVersionMetaKey(lang), DbContext.GetHotspotFamilyVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang), markerFingerprint),
            (DbContext.HotspotFamilyVersionMetaKey, null),
            (DbContext.HotspotFamilyMarkerFingerprintMetaKey, null));
    }

    public void MarkHotspotFamilyMarkerFingerprintIncomplete(string lang, string? markerFingerprint)
    {
        SetMetaValues(
            (DbContext.GetHotspotFamilyVersionMetaKey(lang), DbContext.GetHotspotFamilyVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang), DbContext.BuildIncompleteHotspotFamilyMarkerFingerprint(markerFingerprint)),
            (DbContext.HotspotFamilyVersionMetaKey, null),
            (DbContext.HotspotFamilyMarkerFingerprintMetaKey, null));
    }

    /// <summary>
    /// Demote hotspot-family trust. Called at the start of any indexing run that may leave
    /// a mixed legacy/current symbol set so readers fall back conservatively unless the run
    /// completes and restamps the current version.
    /// hotspot-family trust を縮退させる。index 開始時に呼び、成功時だけ再 stamp する。
    /// </summary>
    public void ClearHotspotFamilyReady()
    {
        var languages = FileIndexer.GetHotspotFamilyMarkerLanguages();
        var keys = new string[2 + (languages.Count * 2)];
        var index = 0;

        keys[index++] = DbContext.HotspotFamilyVersionMetaKey;
        keys[index++] = DbContext.HotspotFamilyMarkerFingerprintMetaKey;
        foreach (var lang in languages)
        {
            keys[index++] = DbContext.GetHotspotFamilyVersionMetaKey(lang);
            keys[index++] = DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang);
        }

        ClearMetaKeys(keys);
    }

    /// <summary>
    /// Stamp the per-language metadata-target version once the writer's resolver has finished
    /// classifying every class-like row for that language. Readers consult this stamp before
    /// trusting `symbols.is_metadata_target`. Issue #435.
    /// 言語別 metadata-target version を stamp する。reader はこの stamp 一致時のみ
    /// `symbols.is_metadata_target` を信頼する。Issue #435。
    /// </summary>
    public void MarkMetadataTargetReady(string lang)
    {
        SetMeta(
            DbContext.GetMetadataTargetVersionMetaKey(lang),
            DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool TypeScriptAugmentationVersionMatchesCurrent()
    {
        return string.Equals(
            GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey),
            DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    public void ClearTypeScriptAugmentationReady()
    {
        SetMeta(DbContext.TypeScriptAugmentationVersionMetaKey, null);
    }
}
