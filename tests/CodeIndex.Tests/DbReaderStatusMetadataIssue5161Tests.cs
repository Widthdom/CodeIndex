using System.Text;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void GetStatus_TreatsClearedStructuredMetadataAsAbsent_Issue5161()
    {
        _writer.SetMeta(DbContext.LastIndexRunReferenceExtractionCapHitsMetaKey, null);
        _writer.SetMeta(DbContext.LastIndexRunRebuildReclaimMetaKey, null);
        _writer.SetMeta(DbContext.LastFailedIndexRunFileErrorsMetaKey, null);

        var status = _reader.GetStatus();

        Assert.Null(status.LastIndexRun);
        Assert.Null(status.LastFailedOrPartialIndexRun);
        Assert.Null(status.StatusMetadataDiagnostics);
    }

    [Fact]
    public void GetStatus_BoundsStructuredMetadataBeforeMaterialization_Issue5161()
    {
        _writer.SetMeta(DbContext.LastIndexRunModeMetaKey, "rebuild");
        var validJson = JsonSerializer.Serialize(
            new StatusRebuildReclaim
            {
                State = "not_needed",
                Reason = "freelist_below_threshold",
                DurationMs = 0,
            },
            StatusMetadataJsonContext.Default.StatusRebuildReclaim);
        var exactLimitJson = validJson + new string(
            ' ',
            StatusMetadataLimits.MaxRawUtf8Bytes - Encoding.UTF8.GetByteCount(validJson));
        Assert.Equal(StatusMetadataLimits.MaxRawUtf8Bytes, Encoding.UTF8.GetByteCount(exactLimitJson));

        _writer.SetMeta(DbContext.LastIndexRunRebuildReclaimMetaKey, exactLimitJson);
        var exactLimitStatus = _reader.GetStatus();
        Assert.Equal("not_needed", exactLimitStatus.LastIndexRun?.RebuildReclaim?.State);
        Assert.Null(exactLimitStatus.StatusMetadataDiagnostics);

        _writer.SetMeta(DbContext.LastIndexRunRebuildReclaimMetaKey, exactLimitJson + " ");
        AssertMetadataDiagnostic(
            _reader.GetStatus(),
            "last_index_run.rebuild_reclaim",
            DbReader.StatusMetadataRawSizeExceededReason,
            StatusMetadataLimits.MaxRawUtf8Bytes + 1L);

        var overDepthJson = "{\"state\":\"not_needed\",\"reason\":\"ok\",\"duration_ms\":0,\"extra\":"
            + new string('[', StatusMetadataLimits.MaxJsonDepth + 1)
            + "0"
            + new string(']', StatusMetadataLimits.MaxJsonDepth + 1)
            + "}";
        _writer.SetMeta(DbContext.LastIndexRunRebuildReclaimMetaKey, overDepthJson);
        AssertMetadataDiagnostic(
            _reader.GetStatus(),
            "last_index_run.rebuild_reclaim",
            DbReader.StatusMetadataInvalidJsonReason,
            Encoding.UTF8.GetByteCount(overDepthJson));

        _writer.SetMeta(DbContext.LastIndexRunRebuildReclaimMetaKey, "{");
        AssertMetadataDiagnostic(
            _reader.GetStatus(),
            "last_index_run.rebuild_reclaim",
            DbReader.StatusMetadataInvalidJsonReason,
            observedUtf8Bytes: 1);
    }

    [Fact]
    public void GetStatus_RejectsFileErrorItemAndNestedStringLimitViolations_Issue5161()
    {
        _writer.SetMeta(DbContext.LastFailedIndexRunStatusMetaKey, "failed");
        var tooManyErrors = Enumerable.Range(0, StatusMetadataLimits.MaxFileErrors + 1)
            .Select(i => new StatusIndexFileError
            {
                File = $"src/File{i}.cs",
                Category = "file_read_error",
                Phase = "reading",
                Detail = "access denied",
            })
            .ToList();
        SetFileErrors(tooManyErrors);

        var countLimitedStatus = _reader.GetStatus();
        Assert.Null(countLimitedStatus.LastFailedOrPartialIndexRun?.FileErrors);
        AssertMetadataDiagnostic(
            countLimitedStatus,
            "last_failed_or_partial_index_run.file_errors",
            DbReader.StatusMetadataSemanticValidationFailedReason);

        SetFileErrors(
        [
            new StatusIndexFileError
            {
                File = new string('p', StatusMetadataLimits.MaxPathCharacters + 1),
                Category = "file_read_error",
                Phase = "reading",
                Detail = "access denied",
            },
        ]);

        var stringLimitedStatus = _reader.GetStatus();
        Assert.Null(stringLimitedStatus.LastFailedOrPartialIndexRun?.FileErrors);
        AssertMetadataDiagnostic(
            stringLimitedStatus,
            "last_failed_or_partial_index_run.file_errors",
            DbReader.StatusMetadataSemanticValidationFailedReason);
    }

    [Fact]
    public void GetStatus_RejectsReferenceCapAndReclaimSemanticViolations_Issue5161()
    {
        _writer.SetMeta(DbContext.LastIndexRunModeMetaKey, "rebuild");
        var tooManyFiles = Enumerable.Range(0, StatusMetadataLimits.MaxReferenceCapHitFiles + 1)
            .Select(i => new ReferenceExtractionFileCapHits
            {
                File = $"src/File{i}.cs",
                HitCount = 1,
                Reasons = ["lookup_symbol_limit"],
            })
            .ToList();
        var invalidCapHits = new ReferenceExtractionCapHitSummary
        {
            HitCount = tooManyFiles.Count,
            AffectedFileCount = tooManyFiles.Count,
            Reasons = ["lookup_symbol_limit"],
            Files = tooManyFiles,
            FilesTruncated = false,
            FileLimit = StatusMetadataLimits.MaxReferenceCapHitFiles,
        };
        _writer.SetMeta(
            DbContext.LastIndexRunReferenceExtractionCapHitsMetaKey,
            JsonSerializer.Serialize(
                invalidCapHits,
                StatusMetadataJsonContext.Default.ReferenceExtractionCapHitSummary));

        var capLimitedStatus = _reader.GetStatus();
        Assert.Null(capLimitedStatus.LastIndexRun?.ReferenceExtractionCapHits);
        AssertMetadataDiagnostic(
            capLimitedStatus,
            "last_index_run.reference_extraction_cap_hits",
            DbReader.StatusMetadataSemanticValidationFailedReason);

        var validCapHits = new ReferenceExtractionCapHitSummary
        {
            FileLimit = StatusMetadataLimits.MaxReferenceCapHitFiles,
        };
        _writer.SetMeta(
            DbContext.LastIndexRunReferenceExtractionCapHitsMetaKey,
            JsonSerializer.Serialize(
                validCapHits,
                StatusMetadataJsonContext.Default.ReferenceExtractionCapHitSummary));
        _writer.SetMeta(
            DbContext.LastIndexRunRebuildReclaimMetaKey,
            JsonSerializer.Serialize(
                new StatusRebuildReclaim
                {
                    State = "completed",
                    Reason = "threshold_reached",
                    DurationMs = -1,
                },
                StatusMetadataJsonContext.Default.StatusRebuildReclaim));

        var reclaimLimitedStatus = _reader.GetStatus();
        Assert.Null(reclaimLimitedStatus.LastIndexRun?.RebuildReclaim);
        AssertMetadataDiagnostic(
            reclaimLimitedStatus,
            "last_index_run.rebuild_reclaim",
            DbReader.StatusMetadataSemanticValidationFailedReason);
    }

    [Fact]
    public void GetStatus_RoundTripsValidStructuredMetadata_Issue5161()
    {
        var capHits = new ReferenceExtractionCapHitSummary
        {
            HitCount = 2,
            AffectedFileCount = 1,
            Reasons = ["lookup_symbol_limit"],
            Files =
            [
                new ReferenceExtractionFileCapHits
                {
                    File = "src/App.cs",
                    HitCount = 2,
                    Reasons = ["lookup_symbol_limit"],
                },
            ],
            FilesTruncated = false,
            FileLimit = StatusMetadataLimits.MaxReferenceCapHitFiles,
        };
        var reclaim = new StatusRebuildReclaim
        {
            State = "completed",
            Reason = "threshold_reached",
            DurationMs = 12,
            PageSizeBytes = 4096,
            PagesReclaimed = 7,
            FreelistRatioBefore = 0.25,
            FreelistRatioAfter = 0.01,
            AutoVacuumMode = 2,
        };
        var fileErrors = new List<StatusIndexFileError>
        {
            new()
            {
                File = "src/Broken.cs",
                Category = "file_read_error",
                Phase = "reading",
                Detail = "access denied",
                Line = 3,
                Column = 4,
            },
        };
        _writer.SetMeta(DbContext.LastIndexRunModeMetaKey, "rebuild");
        _writer.SetMeta(DbContext.LastFailedIndexRunStatusMetaKey, "failed");
        _writer.SetMeta(
            DbContext.LastIndexRunReferenceExtractionCapHitsMetaKey,
            JsonSerializer.Serialize(
                capHits,
                StatusMetadataJsonContext.Default.ReferenceExtractionCapHitSummary));
        _writer.SetMeta(
            DbContext.LastIndexRunRebuildReclaimMetaKey,
            JsonSerializer.Serialize(reclaim, StatusMetadataJsonContext.Default.StatusRebuildReclaim));
        SetFileErrors(fileErrors);

        var status = _reader.GetStatus();

        Assert.Equal(2, status.LastIndexRun?.ReferenceExtractionCapHits?.HitCount);
        Assert.Equal("src/App.cs", status.LastIndexRun?.ReferenceExtractionCapHits?.Files[0].File);
        Assert.Equal(7, status.LastIndexRun?.RebuildReclaim?.PagesReclaimed);
        Assert.Equal("src/Broken.cs", status.LastFailedOrPartialIndexRun?.FileErrors?[0].File);
        Assert.Equal(3, status.LastFailedOrPartialIndexRun?.FileErrors?[0].Line);
        Assert.Null(status.StatusMetadataDiagnostics);
    }

    private void SetFileErrors(List<StatusIndexFileError> fileErrors)
        => _writer.SetMeta(
            DbContext.LastFailedIndexRunFileErrorsMetaKey,
            JsonSerializer.Serialize(
                fileErrors,
                StatusMetadataJsonContext.Default.ListStatusIndexFileError));

    private static void AssertMetadataDiagnostic(
        StatusResult status,
        string field,
        string reason,
        long? observedUtf8Bytes = null)
    {
        var diagnostic = Assert.Single(status.StatusMetadataDiagnostics!);
        Assert.Equal(field, diagnostic.Field);
        Assert.Equal(reason, diagnostic.Reason);
        Assert.Equal(StatusMetadataLimits.MaxRawUtf8Bytes, diagnostic.MaxUtf8Bytes);
        if (observedUtf8Bytes != null)
            Assert.Equal(observedUtf8Bytes, diagnostic.ObservedUtf8Bytes);
    }
}
