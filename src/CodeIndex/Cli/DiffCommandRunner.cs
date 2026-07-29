using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static class DiffCommandRunner
{
    internal const int DefaultDiffLimit = DiffCommandOptionsParser.DefaultLimit;
    internal const int MaxDiffComparedRowsPerSide = 1_000_000;
    internal const int MaxDiffComparedRowBytes = 4 * 1024 * 1024;
    internal const int MinDiffJsonBytes = 4 * 1024;
    internal const int DefaultDiffJsonBytes = 1024 * 1024;
    internal const int MaxDiffJsonBytes = 16 * 1024 * 1024;
    internal static int MaxDiffLimit => QueryCommandRunner.NumericFlagUpperBounds["--limit"];
    internal static int? MaxDiffComparedRowsPerSideForTesting { get; set; }
    internal static int? MaxDiffComparedRowBytesForTesting { get; set; }
    private const int DriftExitCode = 1;
    private const int SchemaMismatchExitCode = 2;
    private const int UnreadableExitCode = 3;

    public static int Run(string[] args, JsonSerializerOptions jsonOptions)
        => Run(args, jsonOptions, CancellationToken.None);

    public static int Run(string[] args, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var options = ParseArgs(args);
        var errorJsonByteBudget = options.MaxJsonBytes
            ?? (options.Json && options.Detailed && !options.SummaryOnly
                ? DefaultDiffJsonBytes
                : null);
        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
            return CommandExitCodes.Success;
        }

        if (options.ParseError is not null)
            return DiffResultWriter.WriteCommandError(
                options.Json || options.SummaryOnly,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Run `cdidx diff <db1> <db2> --help` to see the supported command shape.",
                CommandErrorCodes.UsageError,
                errorJsonByteBudget);

        var json = options.Json || options.SummaryOnly;
        var leftUriValidationExitCode = ValidateReadableDbFileUri(
            options.LeftDb!,
            json,
            jsonOptions,
            errorJsonByteBudget);
        if (leftUriValidationExitCode != null)
            return leftUriValidationExitCode.Value;

        var rightUriValidationExitCode = ValidateReadableDbFileUri(
            options.RightDb!,
            json,
            jsonOptions,
            errorJsonByteBudget);
        if (rightUriValidationExitCode != null)
            return rightUriValidationExitCode.Value;

        try
        {
            var result = CompareDatabases(options, jsonOptions, cancellationToken);
            if (options.CursorSelectionFingerprint is { } cursorSelectionFingerprint
                && (result.SelectionFingerprint is not { } currentSelectionFingerprint
                    || !DiffCursorCodec.FixedTimeEquals(
                        cursorSelectionFingerprint,
                        currentSelectionFingerprint)))
            {
                return DiffResultWriter.WriteCommandError(
                    options.Json || options.SummaryOnly,
                    jsonOptions,
                    "--cursor no longer matches the selected database contents; restart without --cursor",
                    CommandExitCodes.UsageError,
                    "Rerun the detailed diff without --cursor to start from the current deterministic record sequence.",
                    CommandErrorCodes.UsageError,
                    errorJsonByteBudget);
            }

            var outputExitCode = DiffResultWriter.WriteResult(result, options, jsonOptions);
            if (outputExitCode.HasValue)
                return outputExitCode.Value;

            if (result.Status == "schema_mismatch")
                return SchemaMismatchExitCode;
            return result.Identical ? CommandExitCodes.Success : DriftExitCode;
        }
        catch (OperationCanceledException)
        {
            return DiffResultWriter.WriteCommandError(
                options.Json || options.SummaryOnly,
                jsonOptions,
                "diff comparison cancelled before it could complete",
                CommandExitCodes.CancelledBySignal,
                "Retry the diff with a smaller database pair or after the cancelling operation completes.",
                CommandErrorCodes.Interrupted,
                errorJsonByteBudget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            return DiffResultWriter.WriteCommandError(
                options.Json || options.SummaryOnly,
                jsonOptions,
                $"failed to compare databases: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                UnreadableExitCode,
                "Pass two readable CodeIndex SQLite database paths.",
                CommandErrorCodes.DbError,
                errorJsonByteBudget);
        }
    }

    private static int? ValidateReadableDbFileUri(
        string dbPath,
        bool json,
        JsonSerializerOptions jsonOptions,
        int? maxJsonBytes)
    {
        if (SqliteFileUri.TryValidateBounds(dbPath, out var parseError))
            return null;

        return DiffResultWriter.WriteCommandError(
            json,
            jsonOptions,
            $"invalid database file URI: {SqliteFileUri.FormatParseError(parseError)}",
            UnreadableExitCode,
            "Pass two readable CodeIndex SQLite database paths or valid SQLite file URIs.",
            CommandErrorCodes.DbError,
            maxJsonBytes);
    }

    internal static DiffCommandOptions ParseArgs(string[] args)
        => DiffCommandOptionsParser.Parse(args, MaxDiffLimit);

    internal static DiffJsonResult CompareDatabases(
        string leftDb,
        string rightDb,
        int limit,
        int offset,
        bool detailed,
        CancellationToken cancellationToken,
        string? leftDisplayPath = null,
        string? rightDisplayPath = null,
        bool includeContent = false,
        bool emitCursorMetadata = true)
    {
        var options = new DiffCommandOptions
        {
            LeftDb = leftDb,
            RightDb = rightDb,
            Limit = limit,
            Offset = offset,
            Detailed = detailed,
            IncludeContent = includeContent,
            EmitCursorMetadata = emitCursorMetadata,
        };
        var result = CompareDatabasesCore(options, cancellationToken, materializationJsonContext: null);
        return result with
        {
            LeftDb = leftDisplayPath is null
                ? result.LeftDb
                : FormatSensitiveText(leftDisplayPath, options),
            RightDb = rightDisplayPath is null
                ? result.RightDb
                : FormatSensitiveText(rightDisplayPath, options),
        };
    }

    internal static DiffJsonResult CompareDatabases(
        DiffCommandOptions options,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
        => CompareDatabasesCore(
            options,
            cancellationToken,
            CliJsonSerializerContextFactory.Create(jsonOptions));

    private static DiffJsonResult CompareDatabasesCore(
        DiffCommandOptions options,
        CancellationToken cancellationToken,
        CliJsonSerializerContext? materializationJsonContext)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var leftHeader = ReadHeader(options.LeftDb!);
        cancellationToken.ThrowIfCancellationRequested();
        var rightHeader = ReadHeader(options.RightDb!);
        return leftHeader.SchemaVersion != rightHeader.SchemaVersion
            ? BuildSchemaMismatchDiff(leftHeader, rightHeader, options)
            : BuildDiff(
                leftHeader,
                rightHeader,
                options,
                materializationJsonContext,
                cancellationToken);
    }

    private const string FilePathRowsSql = "SELECT path FROM files ORDER BY path";

    private static readonly DiffRowSchema FileRowSchema = new(
        "file",
        ["path", "language", "size", "lines", "checksum"]);
    private static readonly DiffRowSchema ChunkRowSchema = new(
        "chunk",
        ["path", "chunk_index", "start_line", "end_line", "content"]);
    private static readonly DiffRowSchema ReferenceLineRowSchema = new(
        "reference_line",
        ["path", "line", "context"]);
    private static readonly DiffRowSchema FileIssueRowSchema = new(
        "file_issue",
        ["path", "kind", "line", "message"]);
    private static readonly DiffRowSchema SymbolRowSchema = new(
        "symbol",
        [
            "path",
            "kind",
            "sub_kind",
            "name",
            "name_folded",
            "display_name_folded",
            "line",
            "start_line",
            "start_column",
            "end_line",
            "body_start_line",
            "body_end_line",
            "signature",
            "container_kind",
            "container_name",
            "container_qualified_name",
            "family_key",
            "visibility",
            "return_type",
            "is_metadata_target",
            "metadata_target_source",
        ]);
    private static readonly DiffRowSchema LegacyReferenceRowSchema = new(
        "reference",
        [
            "reference_path",
            "symbol_name",
            "symbol_name_folded",
            "reference_kind",
            "line",
            "column",
            "context",
            "has_reference_line",
            "reference_line_path",
            "reference_line_line",
            "reference_line_context",
            "container_kind",
            "container_name",
            "container_name_folded",
        ]);
    private static readonly DiffRowSchema ReferenceRowSchema = new(
        "reference",
        [
            "reference_path",
            "symbol_name",
            "symbol_name_folded",
            "reference_kind",
            "line",
            "column",
            "context",
            "has_reference_line",
            "reference_line_path",
            "reference_line_line",
            "reference_line_context",
            "container_kind",
            "container_name",
            "container_name_folded",
            "source_language",
            "source_path",
            "source_kind",
            "source_container",
            "source_name",
            "source_line",
            "target_qualifier",
            "resolution_state",
            "resolution_candidate_count",
            "is_self_reference",
            "is_mutual_recursion",
            "target_symbol_key",
            "target_language",
            "target_path",
            "target_kind",
            "target_container",
            "target_name",
            "target_line",
            "candidate_scope_rank",
            "candidate_language",
            "candidate_path",
            "candidate_kind",
            "candidate_container",
            "candidate_name",
            "candidate_line",
            "candidate_signature",
        ]);

    private const string FileRowsSql = """
        SELECT
            path,
            lang,
            size,
            lines,
            checksum
        FROM files
        ORDER BY
            path,
            lang,
            size,
            lines,
            checksum
        """;

    private const string ChunkRowsSql = """
        SELECT
            COALESCE(files.path, ''),
            chunks.chunk_index,
            chunks.start_line,
            chunks.end_line,
            chunks.content
        FROM chunks
        LEFT JOIN files ON files.id = chunks.file_id
        ORDER BY
            COALESCE(files.path, ''),
            chunks.chunk_index,
            chunks.start_line,
            chunks.end_line,
            chunks.content
        """;

    private const string ReferenceLineRowsSql = """
        SELECT
            COALESCE(files.path, ''),
            reference_lines.line,
            reference_lines.context
        FROM reference_lines
        LEFT JOIN files ON files.id = reference_lines.file_id
        ORDER BY
            COALESCE(files.path, ''),
            reference_lines.line,
            reference_lines.context
        """;

    private const string FileIssueRowsSql = """
        SELECT
            COALESCE(files.path, ''),
            file_issues.kind,
            file_issues.line,
            file_issues.message
        FROM file_issues
        LEFT JOIN files ON files.id = file_issues.file_id
        ORDER BY
            COALESCE(files.path, ''),
            file_issues.kind,
            file_issues.line,
            file_issues.message
        """;

    private const string ReadinessProvenanceMetaRowsSql = """
        SELECT
            key,
            value
        FROM codeindex_meta
        WHERE
            key <> 'indexed_head_timestamp'
            AND key <> 'last_full_scan_elapsed_ms'
            AND key NOT LIKE 'last_index_run_%'
            AND key NOT LIKE 'last_failed_index_run_%'
        ORDER BY
            key,
            value
        """;

    private const string VolatileTelemetryMetaRowsSql = """
        SELECT
            key,
            value
        FROM codeindex_meta
        WHERE
            key = 'indexed_head_timestamp'
            OR key = 'last_full_scan_elapsed_ms'
            OR key LIKE 'last_index_run_%'
            OR key LIKE 'last_failed_index_run_%'
        ORDER BY
            key,
            value
        """;

    private const string LegacyReferenceRowsSql = """
        SELECT
            COALESCE(files.path, ''),
            symbol_references.symbol_name,
            symbol_references.symbol_name_folded,
            symbol_references.reference_kind,
            symbol_references.line,
            symbol_references.column_number,
            symbol_references.context,
            CASE WHEN symbol_references.reference_line_id IS NULL THEN 0 ELSE 1 END,
            COALESCE(reference_line_files.path, ''),
            reference_lines.line,
            reference_lines.context,
            symbol_references.container_kind,
            symbol_references.container_name,
            symbol_references.container_name_folded
        FROM symbol_references
        LEFT JOIN files ON files.id = symbol_references.file_id
        LEFT JOIN reference_lines ON reference_lines.id = symbol_references.reference_line_id
        LEFT JOIN files AS reference_line_files ON reference_line_files.id = reference_lines.file_id
        ORDER BY
            COALESCE(files.path, ''),
            symbol_references.symbol_name,
            symbol_references.symbol_name_folded,
            symbol_references.reference_kind,
            symbol_references.line,
            symbol_references.column_number,
            symbol_references.context,
            CASE WHEN symbol_references.reference_line_id IS NULL THEN 0 ELSE 1 END,
            COALESCE(reference_line_files.path, ''),
            reference_lines.line,
            reference_lines.context,
            symbol_references.container_kind,
            symbol_references.container_name,
            symbol_references.container_name_folded
        """;

    private static DiffJsonResult BuildDiff(
        DiffDbHeader left,
        DiffDbHeader right,
        DiffCommandOptions options,
        CliJsonSerializerContext? materializationJsonContext,
        CancellationToken cancellationToken)
    {
        var filesOnlyInLeft = new List<string>();
        var filesOnlyInRight = new List<string>();
        var diagnostics = new List<DiffDiagnosticJsonResult>();
        var hasMore = false;
        var dataReasons = new List<string>();
        var readinessProvenanceReasons = new List<string>();
        var telemetryReasons = new List<string>();

        using var leftConnection = OpenReadOnlyConnection(options.LeftDb!);
        using var rightConnection = OpenReadOnlyConnection(options.RightDb!);
        var leftSymbolRowsSql = BuildSymbolRowsSql(leftConnection);
        var rightSymbolRowsSql = BuildSymbolRowsSql(rightConnection);
        var leftReferenceRowsSql = BuildReferenceRowsSql(leftConnection);
        var rightReferenceRowsSql = BuildReferenceRowsSql(rightConnection);
        var leftReferenceRowSchema = GetReferenceRowSchema(leftConnection);
        var rightReferenceRowSchema = GetReferenceRowSchema(rightConnection);

        if (!options.SummaryOnly && !options.Detailed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileDiff = DiffOrderedStrings(leftConnection, rightConnection, FilePathRowsSql, options.Limit, options.Offset, cancellationToken);
            filesOnlyInLeft = fileDiff.OnlyInLeft;
            filesOnlyInRight = fileDiff.OnlyInRight;
            hasMore |= fileDiff.HasMore;
            AddPagingDiagnostic(diagnostics, fileDiff.Omitted, fileDiff.HasMore, "file differences", options);
        }

        using DiffRecordPageCollector? collector = options.Detailed && !options.SummaryOnly
            ? new DiffRecordPageCollector(
                options.LeftDb!,
                options.RightDb!,
                options.Offset,
                options.Limit,
                options.IncludeContent,
                options.DataOnly,
                options.IncludeTelemetry,
                options.Json
                    ? options.MaxJsonBytes ?? DefaultDiffJsonBytes
                    : null,
                materializationJsonContext)
            : null;

        bool fileRowsEqual;
        bool symbolRowsEqual;
        bool referenceRowsEqual;
        bool chunkRowsEqual;
        bool referenceLineRowsEqual;
        bool fileIssueRowsEqual;
        bool readinessProvenanceMetadataEqual;
        bool volatileTelemetryEqual;
        if (collector is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileRowsEqual = CollectOrderedRows(
                leftConnection,
                rightConnection,
                FileRowsSql,
                FileRowsSql,
                FileRowSchema,
                FileRowSchema,
                collector,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            symbolRowsEqual = CollectOrderedRows(
                leftConnection,
                rightConnection,
                leftSymbolRowsSql,
                rightSymbolRowsSql,
                SymbolRowSchema,
                SymbolRowSchema,
                collector,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            referenceRowsEqual = CollectOrderedRows(
                leftConnection,
                rightConnection,
                leftReferenceRowsSql,
                rightReferenceRowsSql,
                leftReferenceRowSchema,
                rightReferenceRowSchema,
                collector,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            chunkRowsEqual = CollectOrderedRows(
                leftConnection,
                rightConnection,
                ChunkRowsSql,
                ChunkRowsSql,
                ChunkRowSchema,
                ChunkRowSchema,
                collector,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            referenceLineRowsEqual = CollectOrderedRows(
                leftConnection,
                rightConnection,
                ReferenceLineRowsSql,
                ReferenceLineRowsSql,
                ReferenceLineRowSchema,
                ReferenceLineRowSchema,
                collector,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            fileIssueRowsEqual = CollectOrderedRows(
                leftConnection,
                rightConnection,
                FileIssueRowsSql,
                FileIssueRowsSql,
                FileIssueRowSchema,
                FileIssueRowSchema,
                collector,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            readinessProvenanceMetadataEqual = CollectMetadataRows(
                leftConnection,
                rightConnection,
                ReadinessProvenanceMetaRowsSql,
                "readiness_provenance_metadata",
                collector,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            volatileTelemetryEqual = options.IncludeTelemetry
                ? CollectMetadataRows(
                    leftConnection,
                    rightConnection,
                    VolatileTelemetryMetaRowsSql,
                    "volatile_telemetry_metadata",
                    collector,
                    cancellationToken)
                : RowsEqual(
                    leftConnection,
                    rightConnection,
                    VolatileTelemetryMetaRowsSql,
                    cancellationToken);
        }
        else
        {
            var countBasedDataDifference =
                left.FileCount != right.FileCount
                || left.SymbolCount != right.SymbolCount
                || left.ReferenceCount != right.ReferenceCount;
            if (countBasedDataDifference)
            {
                fileRowsEqual = left.FileCount == right.FileCount;
                symbolRowsEqual = left.SymbolCount == right.SymbolCount;
                referenceRowsEqual = left.ReferenceCount == right.ReferenceCount;
                chunkRowsEqual = true;
                referenceLineRowsEqual = true;
                fileIssueRowsEqual = true;
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileRowsEqual = RowsEqual(leftConnection, rightConnection, FileRowsSql, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                symbolRowsEqual = RowsEqual(
                    leftConnection,
                    rightConnection,
                    leftSymbolRowsSql,
                    rightSymbolRowsSql,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                referenceRowsEqual = RowsEqual(
                    leftConnection,
                    rightConnection,
                    leftReferenceRowsSql,
                    rightReferenceRowsSql,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                chunkRowsEqual = RowsEqual(leftConnection, rightConnection, ChunkRowsSql, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                referenceLineRowsEqual = RowsEqual(
                    leftConnection,
                    rightConnection,
                    ReferenceLineRowsSql,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                fileIssueRowsEqual = RowsEqual(
                    leftConnection,
                    rightConnection,
                    FileIssueRowsSql,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            readinessProvenanceMetadataEqual = RowsEqual(
                leftConnection,
                rightConnection,
                ReadinessProvenanceMetaRowsSql,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            volatileTelemetryEqual = RowsEqual(
                leftConnection,
                rightConnection,
                VolatileTelemetryMetaRowsSql,
                cancellationToken);
        }

        AddDifferenceReason(dataReasons, fileRowsEqual, "file_rows_changed");
        AddDifferenceReason(dataReasons, symbolRowsEqual, "symbol_rows_changed");
        AddDifferenceReason(dataReasons, referenceRowsEqual, "reference_rows_changed");
        AddDifferenceReason(dataReasons, chunkRowsEqual, "chunk_rows_changed");
        AddDifferenceReason(dataReasons, referenceLineRowsEqual, "reference_line_rows_changed");
        AddDifferenceReason(dataReasons, fileIssueRowsEqual, "file_issue_rows_changed");
        AddDifferenceReason(
            readinessProvenanceReasons,
            readinessProvenanceMetadataEqual,
            "readiness_provenance_metadata_changed");
        AddDifferenceReason(
            telemetryReasons,
            volatileTelemetryEqual,
            "volatile_telemetry_metadata_changed");

        var categories = BuildDifferenceCategories(
            dataReasons,
            schemaReasons: [],
            readinessProvenanceReasons,
            telemetryReasons,
            options,
            evaluated: true);
        var differenceReasons = BuildIncludedDifferenceReasons(categories);
        var identical = differenceReasons.Count == 0;
        var summary = new DiffSummaryJsonResult(
            left.FileCount,
            right.FileCount,
            right.FileCount - left.FileCount,
            left.SymbolCount,
            right.SymbolCount,
            right.SymbolCount - left.SymbolCount,
            left.ReferenceCount,
            right.ReferenceCount,
            right.ReferenceCount - left.ReferenceCount,
            left.SchemaVersion,
            right.SchemaVersion,
            true,
            GetComparisonMode(options),
            differenceReasons.Count,
            differenceReasons,
            categories);

        List<DiffRecordJsonResult>? records = null;
        long? totalCount = null;
        int? returnedCount = null;
        long? omittedCount = null;
        string? selectionFingerprint = null;
        string? currentCursor = null;
        string? nextCursor = null;
        DiffReplayJsonResult? replay = null;
        string? truncationReason = null;
        int? nextOffset = null;
        var truncated = diagnostics.Count > 0;
        if (collector is not null)
        {
            records = collector.Records;
            totalCount = collector.TotalCount;
            returnedCount = records.Count;
            omittedCount = collector.TotalCount - records.Count;
            hasMore = collector.TotalCount > (long)options.Offset + records.Count;
            truncated = omittedCount > 0;
            truncationReason = truncated ? "limit_or_offset" : null;
            if (options.EmitCursorMetadata)
            {
                selectionFingerprint = collector.CompleteSelectionFingerprint();
                currentCursor = DiffCursorCodec.Encode(options.Offset, selectionFingerprint);
                if (hasMore && records.Count > 0)
                {
                    nextOffset = checked(options.Offset + records.Count);
                    nextCursor = DiffCursorCodec.Encode(nextOffset.Value, selectionFingerprint);
                }

                replay = BuildReplayMetadata(options, selectionFingerprint, currentCursor, nextCursor);
            }
            else if (hasMore && records.Count > 0)
            {
                nextOffset = checked(options.Offset + records.Count);
            }
            if (truncated)
            {
                diagnostics.Add(new DiffDiagnosticJsonResult(
                    "diff_records_truncated",
                    hasMore && records.Count > 0 && options.EmitCursorMetadata
                        ? "Detailed diff records were omitted; use replay.next_page_arguments to continue from the next whole record."
                        : hasMore && records.Count > 0
                            ? $"Detailed diff records were omitted; continue from the next whole record with --offset {nextOffset}."
                        : hasMore && options.Limit == 0
                            ? "Detailed diff records were omitted because --limit is 0; rerun with a positive --limit."
                        : "Detailed diff records before the requested offset were omitted from this page."));
            }
        }
        else if (hasMore && options.Limit > 0)
        {
            nextOffset = checked(options.Offset + options.Limit);
        }

        return new DiffJsonResult(
            identical ? "identical" : "different",
            identical,
            FormatSensitiveText(left.Path, options),
            FormatSensitiveText(right.Path, options),
            summary,
            filesOnlyInLeft,
            filesOnlyInRight,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Limit,
            options.Offset,
            options.Detailed,
            hasMore,
            nextOffset,
            truncated,
            diagnostics.Count > 0 ? diagnostics : null,
            Records: records,
            TotalCount: totalCount,
            ReturnedCount: returnedCount,
            OmittedCount: omittedCount,
            ContentIncluded: collector is null ? null : options.IncludeContent,
            ContentPolicy: collector is null ? null : options.IncludeContent ? "included" : "redacted_hashes",
            MaxJsonBytes: options.MaxJsonBytes,
            SelectionFingerprint: selectionFingerprint,
            CurrentCursor: currentCursor,
            NextCursor: nextCursor,
            Replay: replay,
            TruncationReason: truncationReason);
    }

    private static void AddDifferenceReason(List<string> reasons, bool equal, string reason)
    {
        if (!equal)
            reasons.Add(reason);
    }

    private static List<DiffCategorySummaryJsonResult> BuildDifferenceCategories(
        List<string> dataReasons,
        List<string> schemaReasons,
        List<string> readinessProvenanceReasons,
        List<string> telemetryReasons,
        DiffCommandOptions options,
        bool evaluated)
        =>
        [
            BuildDifferenceCategory("data", evaluated, included: true, dataReasons),
            BuildDifferenceCategory("schema", evaluated, included: true, schemaReasons),
            BuildDifferenceCategory(
                "readiness_provenance",
                evaluated,
                included: !options.DataOnly,
                readinessProvenanceReasons),
            BuildDifferenceCategory(
                "volatile_telemetry",
                evaluated,
                included: options.IncludeTelemetry,
                telemetryReasons),
        ];

    private static DiffCategorySummaryJsonResult BuildDifferenceCategory(
        string category,
        bool evaluated,
        bool included,
        List<string> reasons)
        => new(
            category,
            evaluated,
            included,
            reasons.Count > 0,
            reasons.Count,
            reasons);

    private static List<string> BuildIncludedDifferenceReasons(
        List<DiffCategorySummaryJsonResult> categories)
        => categories
            .Where(category => category.Evaluated && category.Included && category.Different)
            .SelectMany(category => category.Reasons.Select(reason => $"{category.Category}:{reason}"))
            .ToList();

    private static string GetComparisonMode(DiffCommandOptions options)
        => options.IncludeTelemetry
            ? "semantic_with_telemetry"
            : options.DataOnly ? "data_only" : "semantic";

    private static void AddPagingDiagnostic(
        List<DiffDiagnosticJsonResult> diagnostics,
        bool omitted,
        bool hasMore,
        string area,
        DiffCommandOptions options)
    {
        if (!omitted)
            return;
        diagnostics.Add(new DiffDiagnosticJsonResult(
            "diff_samples_truncated",
            options.Limit == 0
                ? $"{area} samples were omitted because --limit is 0; rerun with a positive --limit to inspect rows."
                : hasMore
                    ? $"{area} omitted rows outside this page; rerun with --offset {checked(options.Offset + options.Limit)} for the next page."
                    : $"{area} omitted rows before offset {options.Offset}; rerun with a lower --offset to inspect earlier rows."));
    }

    private static DiffJsonResult BuildSchemaMismatchDiff(DiffDbHeader left, DiffDbHeader right, DiffCommandOptions options)
    {
        var categories = new List<DiffCategorySummaryJsonResult>
        {
            BuildDifferenceCategory("data", evaluated: false, included: true, []),
            BuildDifferenceCategory("schema", evaluated: true, included: true, ["schema_version_changed"]),
            BuildDifferenceCategory(
                "readiness_provenance",
                evaluated: false,
                included: !options.DataOnly,
                []),
            BuildDifferenceCategory(
                "volatile_telemetry",
                evaluated: false,
                included: options.IncludeTelemetry,
                []),
        };
        var differenceReasons = BuildIncludedDifferenceReasons(categories);
        var summary = new DiffSummaryJsonResult(
            left.FileCount,
            right.FileCount,
            right.FileCount - left.FileCount,
            left.SymbolCount,
            right.SymbolCount,
            right.SymbolCount - left.SymbolCount,
            left.ReferenceCount,
            right.ReferenceCount,
            right.ReferenceCount - left.ReferenceCount,
            left.SchemaVersion,
            right.SchemaVersion,
            false,
            GetComparisonMode(options),
            differenceReasons.Count,
            differenceReasons,
            categories);
        var selectionFingerprint = options.Detailed && options.EmitCursorMetadata
            ? DiffCursorCodec.CreateSelectionFingerprint(
                options.LeftDb!,
                options.RightDb!,
                options.IncludeContent,
                options.DataOnly,
                options.IncludeTelemetry)
            : null;
        var currentCursor = selectionFingerprint is null || !options.EmitCursorMetadata
            ? null
            : DiffCursorCodec.Encode(options.Offset, selectionFingerprint);

        return new DiffJsonResult(
            "schema_mismatch",
            false,
            FormatSensitiveText(left.Path, options),
            FormatSensitiveText(right.Path, options),
            summary,
            [],
            [],
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Detailed ? [] : null,
            options.Limit,
            options.Offset,
            options.Detailed,
            Records: options.Detailed ? [] : null,
            TotalCount: options.Detailed ? 0 : null,
            ReturnedCount: options.Detailed ? 0 : null,
            OmittedCount: options.Detailed ? 0 : null,
            ContentIncluded: options.Detailed ? options.IncludeContent : null,
            ContentPolicy: options.Detailed ? options.IncludeContent ? "included" : "redacted_hashes" : null,
            MaxJsonBytes: options.MaxJsonBytes,
            SelectionFingerprint: selectionFingerprint,
            CurrentCursor: currentCursor,
            Replay: selectionFingerprint is null || currentCursor is null
                ? null
                : BuildReplayMetadata(options, selectionFingerprint, currentCursor, null));
    }

    private static bool CollectMetadataRows(
        SqliteConnection leftConnection,
        SqliteConnection rightConnection,
        string sql,
        string area,
        DiffRecordPageCollector collector,
        CancellationToken cancellationToken)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = sql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = sql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var leftRowsRead = 0;
        var rightRowsRead = 0;
        var leftHasValue = TryReadMetadataRow(leftReader, out var leftValue, ref leftRowsRead, "left", cancellationToken);
        var rightHasValue = TryReadMetadataRow(rightReader, out var rightValue, ref rightRowsRead, "right", cancellationToken);
        var equal = true;

        while (leftHasValue || rightHasValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparison = leftHasValue && rightHasValue
                ? string.CompareOrdinal(leftValue.Key, rightValue.Key)
                : leftHasValue ? -1 : 1;

            if (comparison == 0)
            {
                if (!string.Equals(leftValue.Value, rightValue.Value, StringComparison.Ordinal))
                {
                    equal = false;
                    collector.AddMetadata(area, leftValue.Key, leftValue.Value, rightValue.Value);
                }

                leftHasValue = TryReadMetadataRow(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
                rightHasValue = TryReadMetadataRow(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
                continue;
            }

            equal = false;
            if (comparison < 0)
            {
                collector.AddMetadata(area, leftValue.Key, leftValue.Value, null);
                leftHasValue = TryReadMetadataRow(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
            }
            else
            {
                collector.AddMetadata(area, rightValue.Key, null, rightValue.Value);
                rightHasValue = TryReadMetadataRow(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
            }
        }

        return equal;
    }

    private static bool CollectOrderedRows(
        SqliteConnection leftConnection,
        SqliteConnection rightConnection,
        string leftSql,
        string rightSql,
        DiffRowSchema leftSchema,
        DiffRowSchema rightSchema,
        DiffRecordPageCollector collector,
        CancellationToken cancellationToken)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = leftSql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = rightSql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var leftRowsRead = 0;
        var rightRowsRead = 0;
        var leftHasValue = TryReadRow(leftReader, out var leftValue, ref leftRowsRead, "left", cancellationToken);
        var rightHasValue = TryReadRow(rightReader, out var rightValue, ref rightRowsRead, "right", cancellationToken);
        var equal = true;

        while (leftHasValue || rightHasValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparison = leftHasValue && rightHasValue
                ? CompareRows(leftValue, rightValue)
                : leftHasValue ? -1 : 1;

            if (comparison == 0)
            {
                leftHasValue = TryReadRow(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
                rightHasValue = TryReadRow(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
                continue;
            }

            equal = false;
            if (comparison < 0)
            {
                collector.Add(leftSchema, "left", leftValue);
                leftHasValue = TryReadRow(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
            }
            else
            {
                collector.Add(rightSchema, "right", rightValue);
                rightHasValue = TryReadRow(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
            }
        }

        return equal;
    }

    private static OrderedRowsDiff DiffOrderedStrings(
        SqliteConnection leftConnection,
        SqliteConnection rightConnection,
        string sql,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = sql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = sql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var onlyInLeft = new List<string>(limit);
        var onlyInRight = new List<string>(limit);
        var leftRowsRead = 0;
        var rightRowsRead = 0;
        var leftHasValue = TryReadString(leftReader, out var leftValue, ref leftRowsRead, "left", cancellationToken);
        var rightHasValue = TryReadString(rightReader, out var rightValue, ref rightRowsRead, "right", cancellationToken);
        var equal = true;
        var leftDifferenceCount = 0;
        var rightDifferenceCount = 0;

        while (leftHasValue || rightHasValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparison = leftHasValue && rightHasValue
                ? string.CompareOrdinal(leftValue, rightValue)
                : leftHasValue ? -1 : 1;

            if (comparison == 0)
            {
                leftHasValue = TryReadString(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
                rightHasValue = TryReadString(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
                continue;
            }

            equal = false;
            if (comparison < 0)
            {
                if (leftDifferenceCount >= offset && onlyInLeft.Count < limit)
                    onlyInLeft.Add(leftValue);
                leftDifferenceCount++;
                leftHasValue = TryReadString(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
            }
            else
            {
                if (rightDifferenceCount >= offset && onlyInRight.Count < limit)
                    onlyInRight.Add(rightValue);
                rightDifferenceCount++;
                rightHasValue = TryReadString(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
            }
        }

        var hasMore =
            leftDifferenceCount > (long)offset + onlyInLeft.Count ||
            rightDifferenceCount > (long)offset + onlyInRight.Count;
        var omitted =
            leftDifferenceCount != onlyInLeft.Count ||
            rightDifferenceCount != onlyInRight.Count;
        return new OrderedRowsDiff(equal, onlyInLeft, onlyInRight, hasMore, omitted);
    }

    private static bool RowsEqual(SqliteConnection leftConnection, SqliteConnection rightConnection, string sql, CancellationToken cancellationToken)
        => RowsEqual(leftConnection, rightConnection, sql, sql, cancellationToken);

    private static bool RowsEqual(SqliteConnection leftConnection, SqliteConnection rightConnection, string leftSql, string rightSql, CancellationToken cancellationToken)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = leftSql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = rightSql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var leftRowsRead = 0;
        var rightRowsRead = 0;
        var leftHasValue = TryReadRow(leftReader, out var leftValue, ref leftRowsRead, "left", cancellationToken);
        var rightHasValue = TryReadRow(rightReader, out var rightValue, ref rightRowsRead, "right", cancellationToken);
        while (leftHasValue && rightHasValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CompareRows(leftValue, rightValue) != 0)
                return false;
            leftHasValue = TryReadRow(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
            rightHasValue = TryReadRow(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
        }

        return leftHasValue == rightHasValue;
    }

    private static bool StringRowsEqual(SqliteConnection leftConnection, SqliteConnection rightConnection, string sql, CancellationToken cancellationToken)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = sql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = sql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var leftRowsRead = 0;
        var rightRowsRead = 0;
        var leftHasValue = TryReadString(leftReader, out var leftValue, ref leftRowsRead, "left", cancellationToken);
        var rightHasValue = TryReadString(rightReader, out var rightValue, ref rightRowsRead, "right", cancellationToken);
        while (leftHasValue && rightHasValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                return false;
            leftHasValue = TryReadString(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
            rightHasValue = TryReadString(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
        }

        return leftHasValue == rightHasValue;
    }

    private static bool TryReadRow(SqliteDataReader reader, out DiffRow value, ref int rowsRead, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!reader.Read())
        {
            value = DiffRow.Empty;
            return false;
        }

        IncrementDiffRowsRead(ref rowsRead, side);
        var sortValues = new object?[reader.FieldCount];
        long rowBytes = 0;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var fieldValue = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rowBytes += EstimateDiffValueBytes(fieldValue);
            EnsureDiffRowByteBudget(rowBytes, side);
            sortValues[i] = fieldValue;
        }

        value = new DiffRow(sortValues);
        return true;
    }

    private static int CompareRows(DiffRow left, DiffRow right)
    {
        var count = Math.Min(left.SortValues.Length, right.SortValues.Length);
        for (var i = 0; i < count; i++)
        {
            var comparison = CompareSqlSortValue(left.SortValues[i], right.SortValues[i]);
            if (comparison != 0)
                return comparison;
        }

        return left.SortValues.Length.CompareTo(right.SortValues.Length);
    }

    private static int CompareSqlSortValue(object? left, object? right)
    {
        var leftRank = GetSqlSortRank(left);
        var rightRank = GetSqlSortRank(right);
        if (leftRank != rightRank)
            return leftRank.CompareTo(rightRank);

        if (leftRank == 0)
            return 0;

        if (leftRank == 1)
        {
            var leftNumber = Convert.ToDecimal(left, System.Globalization.CultureInfo.InvariantCulture);
            var rightNumber = Convert.ToDecimal(right, System.Globalization.CultureInfo.InvariantCulture);
            return leftNumber.CompareTo(rightNumber);
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return CompareBytes(leftBytes, rightBytes);

        var leftText = Convert.ToString(left, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var rightText = Convert.ToString(right, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return string.CompareOrdinal(leftText, rightText);
    }

    private static int GetSqlSortRank(object? value)
    {
        if (value is null or DBNull)
            return 0;
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return 1;
        if (value is byte[])
            return 3;
        return 2;
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        var count = Math.Min(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            var comparison = left[i].CompareTo(right[i]);
            if (comparison != 0)
                return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool TryReadString(SqliteDataReader reader, out string value, ref int rowsRead, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!reader.Read())
        {
            value = string.Empty;
            return false;
        }

        IncrementDiffRowsRead(ref rowsRead, side);
        value = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        EnsureDiffRowByteBudget(EstimateDiffValueBytes(value), side);
        return true;
    }

    private static bool TryReadMetadataRow(SqliteDataReader reader, out MetadataRow value, ref int rowsRead, string side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!reader.Read())
        {
            value = MetadataRow.Empty;
            return false;
        }

        IncrementDiffRowsRead(ref rowsRead, side);
        var key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var rawValue = reader.IsDBNull(1) ? null : reader.GetString(1);
        EnsureDiffRowByteBudget(EstimateDiffValueBytes(key) + EstimateDiffValueBytes(rawValue), side);
        value = new MetadataRow(key, rawValue);
        return true;
    }

    private static void IncrementDiffRowsRead(ref int rowsRead, string side)
    {
        rowsRead++;
        var maxRows = MaxDiffComparedRowsPerSideForTesting ?? MaxDiffComparedRowsPerSide;
        if (rowsRead > maxRows)
            throw new InvalidOperationException($"diff {side} row comparison exceeded the safety budget of {maxRows} rows.");
    }

    private static void EnsureDiffRowByteBudget(long rowBytes, string side)
    {
        var maxBytes = MaxDiffComparedRowBytesForTesting ?? MaxDiffComparedRowBytes;
        if (rowBytes > maxBytes)
            throw new InvalidOperationException($"diff {side} row comparison exceeded the safety budget of {maxBytes} bytes per row.");
    }

    private static long EstimateDiffValueBytes(object? value)
        => value switch
        {
            null or DBNull => 0,
            byte[] bytes => bytes.LongLength,
            string text => Encoding.UTF8.GetByteCount(text),
            _ => Encoding.UTF8.GetByteCount(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
        };

    private static SqliteConnection OpenReadOnlyConnection(string dbPath)
    {
        var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(LongPath.EnsureWindowsPrefix(dbPath)))
            throw new IOException($"database not found: {dbPath}");

        var connection = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
            dbPath,
            pooling: false,
            out _,
            out _);
        connection.Open();
        return connection;
    }

    private static long ExecuteLong(SqliteConnection connection, string sql)
    {
        using var command = SqliteConnectionPolicy.CreateCommand(connection);
        command.CommandText = sql;
        return SqliteCommandPolicy.ReadInt64Scalar(command, "diff database header value");
    }

    private static DiffDbHeader ReadHeader(string dbPath)
    {
        using var connection = OpenReadOnlyConnection(dbPath);

        return new DiffDbHeader(
            DbPathResolver.FormatDbPathForDisplay(dbPath),
            ExecuteLong(connection, "PRAGMA user_version"),
            ExecuteCountIfTableExists(connection, "files"),
            ExecuteCountIfTableExists(connection, "symbols"),
            ExecuteCountIfTableExists(connection, "symbol_references"));
    }

    private static long ExecuteCountIfTableExists(SqliteConnection connection, string table)
    {
        using (var exists = SqliteConnectionPolicy.CreateCommand(connection))
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
            SqliteCommandPolicy.AddText(exists, "$table", table);
            if (exists.ExecuteScalar() is null)
                return 0;
        }

        using var command = SqliteConnectionPolicy.CreateCommand(connection);
        command.CommandText = SqliteCommandPolicy.CountRowsSql(table);
        return SqliteCommandPolicy.ReadInt64Scalar(command, $"diff table row count {table}");
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = SqliteConnectionPolicy.CreateCommand(connection);
        command.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = SqliteConnectionPolicy.CreateCommand(connection);
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
        SqliteCommandPolicy.AddText(command, "$table", table);
        return command.ExecuteScalar() is not null;
    }

    private static string BuildReferenceRowsSql(SqliteConnection connection)
    {
        if (!HasResolvedReferenceRowSchema(connection))
            return LegacyReferenceRowsSql;

        return """
            SELECT
                COALESCE(reference_files.path, ''),
                r.symbol_name,
                r.symbol_name_folded,
                r.reference_kind,
                r.line,
                r.column_number,
                r.context,
                CASE WHEN r.reference_line_id IS NULL THEN 0 ELSE 1 END,
                COALESCE(reference_line_files.path, ''),
                reference_lines.line,
                reference_lines.context,
                r.container_kind,
                r.container_name,
                r.container_name_folded,
                source_files.lang,
                source_files.path,
                source_symbols.kind,
                COALESCE(source_symbols.container_qualified_name, source_symbols.container_name),
                source_symbols.name,
                source_symbols.line,
                r.target_qualifier,
                r.resolution_state,
                r.resolution_candidate_count,
                r.is_self_reference,
                r.is_mutual_recursion,
                r.target_symbol_key,
                target_files.lang,
                target_files.path,
                target_symbols.kind,
                COALESCE(target_symbols.container_qualified_name, target_symbols.container_name),
                target_symbols.name,
                target_symbols.line,
                candidates.scope_rank,
                candidate_files.lang,
                candidate_files.path,
                candidate_symbols.kind,
                COALESCE(candidate_symbols.container_qualified_name, candidate_symbols.container_name),
                candidate_symbols.name,
                candidate_symbols.line,
                candidate_symbols.signature
            FROM symbol_references AS r
            LEFT JOIN files AS reference_files ON reference_files.id = r.file_id
            LEFT JOIN reference_lines ON reference_lines.id = r.reference_line_id
            LEFT JOIN files AS reference_line_files ON reference_line_files.id = reference_lines.file_id
            LEFT JOIN symbols AS source_symbols ON source_symbols.id = r.source_symbol_id
            LEFT JOIN files AS source_files ON source_files.id = source_symbols.file_id
            LEFT JOIN symbols AS target_symbols ON target_symbols.id = r.target_symbol_id
            LEFT JOIN files AS target_files ON target_files.id = target_symbols.file_id
            LEFT JOIN symbol_reference_candidates AS candidates ON candidates.reference_id = r.id
            LEFT JOIN symbols AS candidate_symbols ON candidate_symbols.id = candidates.symbol_id
            LEFT JOIN files AS candidate_files ON candidate_files.id = candidate_symbols.file_id
            ORDER BY
                1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
                11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
                21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
                31, 32, 33, 34, 35, 36, 37, 38, 39, 40
            """;
    }

    private static DiffRowSchema GetReferenceRowSchema(SqliteConnection connection)
        => HasResolvedReferenceRowSchema(connection)
            ? ReferenceRowSchema
            : LegacyReferenceRowSchema;

    private static bool HasResolvedReferenceRowSchema(SqliteConnection connection)
        => TableExists(connection, "symbol_reference_candidates")
            && ColumnExists(connection, "symbol_references", "source_symbol_id")
            && ColumnExists(connection, "symbol_references", "target_symbol_id")
            && ColumnExists(connection, "symbol_references", "target_symbol_key")
            && ColumnExists(connection, "symbol_references", "target_qualifier")
            && ColumnExists(connection, "symbol_references", "resolution_state")
            && ColumnExists(connection, "symbol_references", "resolution_candidate_count")
            && ColumnExists(connection, "symbols", "container_qualified_name");

    private static string BuildSymbolRowsSql(SqliteConnection connection)
    {
        var metadataTargetExpr = ColumnExists(connection, "symbols", "is_metadata_target")
            ? "symbols.is_metadata_target"
            : "NULL";
        var metadataTargetSourceExpr = ColumnExists(connection, "symbols", "metadata_target_source")
            ? "symbols.metadata_target_source"
            : "NULL";
        var displayNameFoldedExpr = ColumnExists(connection, "symbols", "display_name_folded")
            ? "symbols.display_name_folded"
            : "NULL";

        return $$"""
            SELECT
                COALESCE(files.path, ''),
                symbols.kind,
                symbols.sub_kind,
                symbols.name,
                symbols.name_folded,
                {{displayNameFoldedExpr}},
                symbols.line,
                symbols.start_line,
                symbols.start_column,
                symbols.end_line,
                symbols.body_start_line,
                symbols.body_end_line,
                symbols.signature,
                symbols.container_kind,
                symbols.container_name,
                symbols.container_qualified_name,
                symbols.family_key,
                symbols.visibility,
                symbols.return_type,
                {{metadataTargetExpr}},
                {{metadataTargetSourceExpr}}
            FROM symbols
            LEFT JOIN files ON files.id = symbols.file_id
            ORDER BY
                COALESCE(files.path, ''),
                symbols.kind,
                symbols.sub_kind,
                symbols.name,
                symbols.name_folded,
                {{displayNameFoldedExpr}},
                symbols.line,
                symbols.start_line,
                symbols.start_column,
                symbols.end_line,
                symbols.body_start_line,
                symbols.body_end_line,
                symbols.signature,
                symbols.container_kind,
                symbols.container_name,
                symbols.container_qualified_name,
                symbols.family_key,
                symbols.visibility,
                symbols.return_type,
                {{metadataTargetExpr}},
                {{metadataTargetSourceExpr}}
            """;
    }

    internal static DiffReplayJsonResult BuildReplayMetadata(
        DiffCommandOptions options,
        string selectionFingerprint,
        string currentCursor,
        string? nextCursor,
        int? effectiveMaxJsonBytes = null)
    {
        List<string>? nextPageArguments = null;
        if (nextCursor is not null)
        {
            nextPageArguments =
            [
                "--detailed",
                "--json",
                "--limit",
                options.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--cursor",
                nextCursor,
            ];
            if (options.IncludeContent)
                nextPageArguments.Add("--include-content");
            if (options.DataOnly)
                nextPageArguments.Add("--data-only");
            if (options.IncludeTelemetry)
                nextPageArguments.Add("--include-telemetry");
            var replayMaxJsonBytes = effectiveMaxJsonBytes ?? options.MaxJsonBytes;
            if (replayMaxJsonBytes.HasValue)
            {
                nextPageArguments.Add("--max-json-bytes");
                nextPageArguments.Add(replayMaxJsonBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return new DiffReplayJsonResult(
            DiffCursorCodec.Prefix.TrimEnd(':'),
            selectionFingerprint,
            currentCursor,
            nextCursor,
            nextPageArguments);
    }

    private static string FormatSensitiveText(string value, DiffCommandOptions options)
    {
        if (!options.Detailed || options.IncludeContent)
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = HexEncoding.ToLowerHexString(SHA256.HashData(bytes));
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"sha256:{hash} ({bytes.LongLength} UTF-8 bytes)");
    }

    private static string CreateDiffRecordIdentity(
        DiffRowSchema schema,
        DiffRow row)
    {
        if (schema.FieldNames.Length != row.SortValues.Length)
        {
            throw new InvalidOperationException(
                $"diff {schema.Area} row schema expected {schema.FieldNames.Length} fields but read {row.SortValues.Length}.");
        }

        using var identityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendIdentityPart(identityHash, schema.Area);
        for (var i = 0; i < row.SortValues.Length; i++)
        {
            var name = schema.FieldNames[i];
            var value = row.SortValues[i];
            AppendIdentityPart(identityHash, name);
            AppendIdentityValue(identityHash, value);
        }

        return HexEncoding.ToLowerHexString(identityHash.GetHashAndReset());
    }

    private static DiffRecordJsonResult CreateDiffRecord(
        DiffRowSchema schema,
        string side,
        DiffRow row,
        string identitySha256,
        bool includeContent)
    {
        var fields = new List<DiffFieldJsonResult>(row.SortValues.Length);
        for (var i = 0; i < row.SortValues.Length; i++)
            fields.Add(CreateDiffField(schema.FieldNames[i], row.SortValues[i], includeContent));

        return new DiffRecordJsonResult(
            schema.Area,
            side,
            identitySha256,
            fields);
    }

    private static DiffFieldJsonResult CreateDiffField(string name, object? rawValue, bool includeContent)
    {
        if (rawValue is null or DBNull)
            return new DiffFieldJsonResult(name, "null", null, null, null, 0, false);

        if (rawValue is byte[] binary)
        {
            return new DiffFieldJsonResult(
                name,
                "blob",
                includeContent ? Convert.ToBase64String(binary) : null,
                includeContent ? "base64" : null,
                HexEncoding.ToLowerHexString(SHA256.HashData(binary)),
                binary.LongLength,
                !includeContent);
        }

        var value = Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(value);
        var valueType = rawValue is byte or sbyte or short or ushort or int or uint or long or ulong
            ? "integer"
            : rawValue is float or double or decimal
                ? "real"
                : "text";
        var redact = valueType == "text" && !includeContent;
        return new DiffFieldJsonResult(
            name,
            valueType,
            redact ? null : value,
            redact || valueType != "text" ? null : "utf-8",
            valueType == "text"
                ? HexEncoding.ToLowerHexString(SHA256.HashData(bytes))
                : null,
            bytes.LongLength,
            redact);
    }

    private static void AppendIdentityPart(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendIdentityValue(IncrementalHash hash, object? rawValue)
    {
        if (rawValue is null or DBNull)
        {
            AppendIdentityPart(hash, "null");
            return;
        }

        if (rawValue is byte[] binary)
        {
            AppendIdentityPart(hash, "blob");
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, binary.Length);
            hash.AppendData(length);
            hash.AppendData(binary);
            return;
        }

        AppendIdentityPart(hash, rawValue.GetType().FullName ?? rawValue.GetType().Name);
        AppendIdentityPart(
            hash,
            Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private sealed record OrderedRowsDiff(
        bool Equal,
        List<string> OnlyInLeft,
        List<string> OnlyInRight,
        bool HasMore,
        bool Omitted);

    private sealed record DiffRowSchema(
        string Area,
        string[] FieldNames);

    private sealed class DiffRecordPageCollector : IDisposable
    {
        private readonly int _offset;
        private readonly int _limit;
        private readonly bool _includeContent;
        private readonly int? _materializationByteBudget;
        private readonly CliJsonSerializerContext _materializationJsonContext;
        private readonly IncrementalHash _selectionHash;
        private long _materializedRecordBytes;
        private bool _materializationBudgetReached;
        private bool _selectionFingerprintCompleted;

        internal DiffRecordPageCollector(
            string leftDb,
            string rightDb,
            int offset,
            int limit,
            bool includeContent,
            bool dataOnly,
            bool includeTelemetry,
            int? materializationByteBudget,
            CliJsonSerializerContext? materializationJsonContext)
        {
            _offset = offset;
            _limit = limit;
            _includeContent = includeContent;
            _materializationByteBudget = materializationByteBudget;
            _materializationJsonContext = materializationJsonContext
                ?? CliJsonSerializerContext.Default;
            _selectionHash = DiffCursorCodec.CreateSelectionHash(
                leftDb,
                rightDb,
                includeContent,
                dataOnly,
                includeTelemetry);
        }

        internal List<DiffRecordJsonResult> Records { get; } = [];
        internal long TotalCount { get; private set; }

        internal void Add(DiffRowSchema schema, string side, DiffRow row)
        {
            var identitySha256 = CreateDiffRecordIdentity(schema, row);
            DiffCursorCodec.AppendSelectionRecord(
                _selectionHash,
                schema.Area,
                side,
                identitySha256);
            if (TotalCount >= _offset &&
                Records.Count < _limit &&
                !_materializationBudgetReached)
            {
                var record = CreateDiffRecord(
                    schema,
                    side,
                    row,
                    identitySha256,
                    _includeContent);
                if (_materializationByteBudget is null)
                {
                    Records.Add(record);
                }
                else
                {
                    var recordBytes = JsonSerializer.SerializeToUtf8Bytes(
                        record,
                        _materializationJsonContext.DiffRecordJsonResult).LongLength;
                    Records.Add(record);
                    _materializedRecordBytes = checked(_materializedRecordBytes + recordBytes);
                    _materializationBudgetReached =
                        _materializedRecordBytes > _materializationByteBudget.Value;
                }
            }
            TotalCount++;
        }

        internal string CompleteSelectionFingerprint()
        {
            if (_selectionFingerprintCompleted)
                throw new InvalidOperationException("diff selection fingerprint was already completed");

            _selectionFingerprintCompleted = true;
            return DiffCursorCodec.CompleteSelectionFingerprint(_selectionHash);
        }

        internal void AddMetadata(string area, string key, string? leftValue, string? rightValue)
            => Add(
                new DiffRowSchema(area, ["key", "left_value", "right_value"]),
                "changed",
                new DiffRow([key, leftValue, rightValue]));

        public void Dispose()
            => _selectionHash.Dispose();
    }

    private sealed record DiffRow(
        object?[] SortValues)
    {
        public static readonly DiffRow Empty = new([]);
    }

    private readonly record struct MetadataRow(
        string Key,
        string? Value)
    {
        public static readonly MetadataRow Empty = new(string.Empty, null);
    }

    private sealed record DiffDbHeader(
        string Path,
        long SchemaVersion,
        long FileCount,
        long SymbolCount,
        long ReferenceCount);
}

internal sealed class DiffCommandOptions
{
    public string? LeftDb { get; init; }
    public string? RightDb { get; init; }
    public bool Json { get; init; }
    public bool Detailed { get; init; }
    public bool SummaryOnly { get; init; }
    public bool IncludeContent { get; init; }
    public bool DataOnly { get; init; }
    public bool IncludeTelemetry { get; init; }
    public bool ShowHelp { get; init; }
    public int Limit { get; init; } = 20;
    public int Offset { get; init; }
    public bool OffsetExplicit { get; init; }
    public int? MaxJsonBytes { get; init; }
    public string? Cursor { get; init; }
    public string? CursorSelectionFingerprint { get; init; }
    public bool EmitCursorMetadata { get; init; } = true;
    public string? ParseError { get; init; }
}
