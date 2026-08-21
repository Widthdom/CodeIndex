namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Stamp the cdidx version string that wrote the most recent successful end-of-index
    /// pass. Readers compare this against their own binary version (and each persisted
    /// contract version) to surface forward-compatibility warnings when an older cdidx
    /// opens a DB last written by a newer cdidx. Issue #1515.
    /// 成功 index 末尾で書き込みを行った cdidx の version を stamp する。reader は自身の
    /// version と各 contract version と突き合わせて forward-compat 警告を出す。Issue #1515。
    /// </summary>
    public void WriteCdidxWriterVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;
        SetMeta(DbContext.CdidxWriterVersionMetaKey, version);
    }

    public void ClearLastFailedIndexRunMetadata()
    {
        ClearMetaKeys(
            DbContext.LastFailedIndexRunStatusMetaKey,
            DbContext.LastFailedIndexRunModeMetaKey,
            DbContext.LastFailedIndexRunStartedAtMetaKey,
            DbContext.LastFailedIndexRunDurationMsMetaKey,
            DbContext.LastFailedIndexRunFilesProcessedMetaKey,
            DbContext.LastFailedIndexRunFilesTotalMetaKey,
            DbContext.LastFailedIndexRunErrorCodeMetaKey,
            DbContext.LastFailedIndexRunReasonMetaKey,
            DbContext.LastFailedIndexRunProgressPersistedMetaKey,
            DbContext.LastFailedIndexRunRecoveryHintMetaKey,
            DbContext.LastFailedIndexRunFileErrorsMetaKey);
    }

    public void MarkIndexComplete()
    {
        SetMetaValues(
            (DbContext.IndexCompletenessMetaKey, "complete"),
            (DbContext.IndexIncompleteReasonsMetaKey, null));
    }

    public void MarkIndexIncomplete(IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        SetMetaValues(
            (DbContext.IndexCompletenessMetaKey, "incomplete"),
            (DbContext.IndexIncompleteReasonsMetaKey, JsonStringListCodec.Serialize(reasons)));
    }

    public void MarkIndexCompleteness(IReadOnlyList<string> incompleteReasons)
    {
        ArgumentNullException.ThrowIfNull(incompleteReasons);
        if (incompleteReasons.Count == 0)
            MarkIndexComplete();
        else
            MarkIndexIncomplete(incompleteReasons);
    }

    /// <summary>
    /// Stamp unknown-extension scan coverage from the latest successful full-worktree scan.
    /// Stores the total count plus a bounded path sample so status callers can identify the
    /// first files that need a language mapping or ignore rule without unbounded metadata.
    /// 未知拡張子の scan coverage を保存する。件数と上限付き path sample を status で返す。
    /// </summary>
    public void WriteUnknownExtensionFileMetadata(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            SetMetaValues(
                (DbContext.UnknownExtensionFileCountMetaKey, "0"),
                (DbContext.UnknownExtensionFilePathsMetaKey, "[]"),
                (DbContext.UnknownExtensionFilesTruncatedMetaKey, false.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.UnknownExtensionFilePathLimitMetaKey, DbContext.UnknownExtensionFilePathSampleLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.UnknownExtensionExtensionCountsMetaKey, "{}"),
                (DbContext.UnknownExtensionCategoryCountsMetaKey, "{}"),
                (DbContext.UnknownExtensionGroupsMetaKey, "[]"),
                (DbContext.UnknownExtensionGroupCountMetaKey, "0"),
                (DbContext.UnknownExtensionGroupsTruncatedMetaKey, false.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.UnknownExtensionGroupLimitMetaKey, UnknownExtensionClassifier.MaxPersistedGroups.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.UnknownExtensionGroupOmittedCountMetaKey, "0"));
            return;
        }

        var sample = JsonStringListCodec.TakeSerializableSample(
            paths,
            DbContext.UnknownExtensionFilePathSampleLimit);
        var classification = UnknownExtensionClassifier.Classify(paths);
        SetMetaValues(
            (DbContext.UnknownExtensionFileCountMetaKey, paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionFilePathsMetaKey, JsonStringListCodec.Serialize(sample)),
            (DbContext.UnknownExtensionFilesTruncatedMetaKey, (paths.Count > sample.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionFilePathLimitMetaKey, DbContext.UnknownExtensionFilePathSampleLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionExtensionCountsMetaKey, UnknownExtensionClassifier.SerializeCounts(classification.ExtensionCounts)),
            (DbContext.UnknownExtensionCategoryCountsMetaKey, UnknownExtensionClassifier.SerializeCounts(classification.CategoryCounts)),
            (DbContext.UnknownExtensionGroupsMetaKey, UnknownExtensionClassifier.SerializeGroups(classification.Groups)),
            (DbContext.UnknownExtensionGroupCountMetaKey, classification.GroupCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionGroupsTruncatedMetaKey, classification.GroupsTruncated.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionGroupLimitMetaKey, classification.GroupLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionGroupOmittedCountMetaKey, classification.GroupOmittedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
