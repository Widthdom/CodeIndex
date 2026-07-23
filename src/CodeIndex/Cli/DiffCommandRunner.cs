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
    internal const int MaxDiffEncodedFieldSampleLength = 1024;
    internal const int MaxDiffComparedRowsPerSide = 1_000_000;
    internal const int MaxDiffComparedRowBytes = 4 * 1024 * 1024;
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
                CommandErrorCodes.UsageError);

        var json = options.Json || options.SummaryOnly;
        var leftUriValidationExitCode = ValidateReadableDbFileUri(options.LeftDb!, json, jsonOptions);
        if (leftUriValidationExitCode != null)
            return leftUriValidationExitCode.Value;

        var rightUriValidationExitCode = ValidateReadableDbFileUri(options.RightDb!, json, jsonOptions);
        if (rightUriValidationExitCode != null)
            return rightUriValidationExitCode.Value;

        try
        {
            var result = CompareDatabases(options, cancellationToken);

            DiffResultWriter.WriteResult(result, options, jsonOptions);

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
                CommandErrorCodes.Interrupted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            return DiffResultWriter.WriteCommandError(
                options.Json || options.SummaryOnly,
                jsonOptions,
                $"failed to compare databases: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                UnreadableExitCode,
                "Pass two readable CodeIndex SQLite database paths.",
                CommandErrorCodes.DbError);
        }
    }

    private static int? ValidateReadableDbFileUri(string dbPath, bool json, JsonSerializerOptions jsonOptions)
    {
        if (SqliteFileUri.TryValidateBounds(dbPath, out var parseError))
            return null;

        return DiffResultWriter.WriteCommandError(
            json,
            jsonOptions,
            $"invalid database file URI: {SqliteFileUri.FormatParseError(parseError)}",
            UnreadableExitCode,
            "Pass two readable CodeIndex SQLite database paths or valid SQLite file URIs.",
            CommandErrorCodes.DbError);
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
        string? rightDisplayPath = null)
    {
        var options = new DiffCommandOptions
        {
            LeftDb = leftDb,
            RightDb = rightDb,
            Limit = limit,
            Offset = offset,
            Detailed = detailed,
        };
        var result = CompareDatabases(options, cancellationToken);
        return result with
        {
            LeftDb = leftDisplayPath ?? result.LeftDb,
            RightDb = rightDisplayPath ?? result.RightDb,
        };
    }

    private static DiffJsonResult CompareDatabases(DiffCommandOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var leftHeader = ReadHeader(options.LeftDb!);
        cancellationToken.ThrowIfCancellationRequested();
        var rightHeader = ReadHeader(options.RightDb!);
        return leftHeader.SchemaVersion != rightHeader.SchemaVersion
            ? BuildSchemaMismatchDiff(leftHeader, rightHeader, options)
            : BuildDiff(leftHeader, rightHeader, options, cancellationToken);
    }

    private const string FilePathRowsSql = "SELECT path FROM files ORDER BY path";

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

    private const string MetaRowsSql = """
        SELECT
            key,
            value
        FROM codeindex_meta
        WHERE
            key = 'hotspot_family_version'
            OR key = 'hotspot_family_marker_fingerprint'
            OR key LIKE 'hotspot_family_version_%'
            OR key LIKE 'hotspot_family_marker_fingerprint_%'
            OR key = 'csharp_symbol_name_contract_version'
            OR key = 'sql_graph_contract_version'
            OR key LIKE 'symbol_extractor_version_%'
            OR key LIKE 'metadata_target_version_%'
        ORDER BY
            key,
            value
        """;

    private const string OperationalMetaRowsSql = """
        SELECT
            key,
            value
        FROM codeindex_meta
        WHERE
            key = 'indexed_project_root'
            OR key = 'indexed_follow_symlinks_policy'
            OR key = 'indexed_head_commit'
            OR key = 'indexed_head_commit_branch'
            OR key = 'indexed_head_sha'
            OR key = 'indexed_head_branch'
            OR key = 'indexed_head_timestamp'
            OR key = 'commit_scoped_fresh_head_sha'
            OR key = 'workspace_path_case_sensitive'
            OR key LIKE 'last_index_run_%'
            OR key = 'unknown_extension_file_count'
            OR key = 'unknown_extension_file_paths_json'
            OR key = 'unknown_extension_files_truncated'
            OR key = 'unknown_extension_file_path_limit'
            OR key = 'unknown_extension_extension_counts_json'
            OR key = 'unknown_extension_category_counts_json'
            OR key = 'unknown_extension_groups_json'
            OR key = 'cdidx_writer_version'
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

    private static DiffJsonResult BuildDiff(DiffDbHeader left, DiffDbHeader right, DiffCommandOptions options, CancellationToken cancellationToken)
    {
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
            left.SchemaVersion == right.SchemaVersion);

        var filesOnlyInLeft = new List<string>();
        var filesOnlyInRight = new List<string>();
        var symbolsOnlyInLeft = new List<string>();
        var symbolsOnlyInRight = new List<string>();
        var referencesOnlyInLeft = new List<string>();
        var referencesOnlyInRight = new List<string>();
        var chunksOnlyInLeft = new List<string>();
        var chunksOnlyInRight = new List<string>();
        List<DiffMetadataDriftJsonResult>? metadataDrift = null;
        var diagnostics = new List<DiffDiagnosticJsonResult>();
        var hasMore = false;
        var identical =
            summary.SchemaVersionsEqual &&
            summary.FileCountDelta == 0 &&
            summary.SymbolCountDelta == 0 &&
            summary.ReferenceCountDelta == 0;

        using var leftConnection = OpenReadOnlyConnection(options.LeftDb!);
        using var rightConnection = OpenReadOnlyConnection(options.RightDb!);
        var leftSymbolRowsSql = BuildSymbolRowsSql(leftConnection);
        var rightSymbolRowsSql = BuildSymbolRowsSql(rightConnection);
        var leftReferenceRowsSql = BuildReferenceRowsSql(leftConnection);
        var rightReferenceRowsSql = BuildReferenceRowsSql(rightConnection);

        if (!options.SummaryOnly)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileDiff = DiffOrderedStrings(leftConnection, rightConnection, FilePathRowsSql, options.Limit, options.Offset, cancellationToken);
            filesOnlyInLeft = fileDiff.OnlyInLeft;
            filesOnlyInRight = fileDiff.OnlyInRight;
            identical = identical && fileDiff.Equal;
            hasMore |= fileDiff.HasMore;
            AddPagingDiagnostic(diagnostics, fileDiff.Omitted, fileDiff.HasMore, "file differences", options);
        }

        if (options.Detailed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbolDiff = DiffOrderedRows(leftConnection, rightConnection, leftSymbolRowsSql, rightSymbolRowsSql, options.Limit, options.Offset, cancellationToken);
            symbolsOnlyInLeft = symbolDiff.OnlyInLeft;
            symbolsOnlyInRight = symbolDiff.OnlyInRight;
            identical = identical && symbolDiff.Equal;
            hasMore |= symbolDiff.HasMore;
            AddPagingDiagnostic(diagnostics, symbolDiff.Omitted, symbolDiff.HasMore, "symbol differences", options);

            cancellationToken.ThrowIfCancellationRequested();
            var referenceDiff = DiffOrderedRows(leftConnection, rightConnection, leftReferenceRowsSql, rightReferenceRowsSql, options.Limit, options.Offset, cancellationToken);
            referencesOnlyInLeft = referenceDiff.OnlyInLeft;
            referencesOnlyInRight = referenceDiff.OnlyInRight;
            identical = identical && referenceDiff.Equal;
            hasMore |= referenceDiff.HasMore;
            AddPagingDiagnostic(diagnostics, referenceDiff.Omitted, referenceDiff.HasMore, "reference-edge differences", options);

            cancellationToken.ThrowIfCancellationRequested();
            var chunkDiff = DiffOrderedRows(leftConnection, rightConnection, ChunkRowsSql, ChunkRowsSql, options.Limit, options.Offset, cancellationToken);
            chunksOnlyInLeft = chunkDiff.OnlyInLeft;
            chunksOnlyInRight = chunkDiff.OnlyInRight;
            identical = identical && chunkDiff.Equal;
            hasMore |= chunkDiff.HasMore;
            AddPagingDiagnostic(diagnostics, chunkDiff.Omitted, chunkDiff.HasMore, "chunk differences", options);

            cancellationToken.ThrowIfCancellationRequested();
            var metadataDiff = DiffMetadataRows(leftConnection, rightConnection, options.Limit, options.Offset, cancellationToken);
            metadataDrift = metadataDiff.Drift;
            hasMore |= metadataDiff.HasMore;
            AddPagingDiagnostic(diagnostics, metadataDiff.Omitted, metadataDiff.HasMore, "metadata differences", options);
        }

        if (identical)
        {
            cancellationToken.ThrowIfCancellationRequested();
            identical =
                RowsEqual(leftConnection, rightConnection, FileRowsSql, cancellationToken) &&
                (options.Detailed || RowsEqual(leftConnection, rightConnection, ChunkRowsSql, cancellationToken)) &&
                RowsEqual(leftConnection, rightConnection, ReferenceLineRowsSql, cancellationToken) &&
                RowsEqual(leftConnection, rightConnection, FileIssueRowsSql, cancellationToken) &&
                RowsEqual(leftConnection, rightConnection, MetaRowsSql, cancellationToken) &&
                (options.Detailed || RowsEqual(leftConnection, rightConnection, leftSymbolRowsSql, rightSymbolRowsSql, cancellationToken)) &&
                (options.Detailed || RowsEqual(leftConnection, rightConnection, leftReferenceRowsSql, rightReferenceRowsSql, cancellationToken));
        }

        var truncated = diagnostics.Count > 0;
        return new DiffJsonResult(
            identical ? "identical" : "different",
            identical,
            left.Path,
            right.Path,
            summary,
            filesOnlyInLeft,
            filesOnlyInRight,
            options.Detailed ? symbolsOnlyInLeft : null,
            options.Detailed ? symbolsOnlyInRight : null,
            options.Detailed ? referencesOnlyInLeft : null,
            options.Detailed ? referencesOnlyInRight : null,
            options.Detailed ? chunksOnlyInLeft : null,
            options.Detailed ? chunksOnlyInRight : null,
            options.Detailed ? metadataDrift : null,
            options.Limit,
            options.Offset,
            options.Detailed,
            hasMore,
            hasMore && options.Limit > 0 ? checked(options.Offset + options.Limit) : null,
            truncated,
            truncated ? diagnostics : null);
    }

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
            false);

        return new DiffJsonResult(
            "schema_mismatch",
            false,
            left.Path,
            right.Path,
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
            options.Detailed);
    }

    private static MetadataRowsDiff DiffMetadataRows(
        SqliteConnection leftConnection,
        SqliteConnection rightConnection,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = OperationalMetaRowsSql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = OperationalMetaRowsSql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var drift = new List<DiffMetadataDriftJsonResult>(limit);
        var leftRowsRead = 0;
        var rightRowsRead = 0;
        var leftHasValue = TryReadMetadataRow(leftReader, out var leftValue, ref leftRowsRead, "left", cancellationToken);
        var rightHasValue = TryReadMetadataRow(rightReader, out var rightValue, ref rightRowsRead, "right", cancellationToken);
        var equal = true;
        var differenceCount = 0;

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
                    if (differenceCount >= offset && drift.Count < limit)
                        drift.Add(new DiffMetadataDriftJsonResult(leftValue.Key, leftValue.Value, rightValue.Value));
                    differenceCount++;
                }

                leftHasValue = TryReadMetadataRow(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
                rightHasValue = TryReadMetadataRow(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
                continue;
            }

            equal = false;
            if (comparison < 0)
            {
                if (differenceCount >= offset && drift.Count < limit)
                    drift.Add(new DiffMetadataDriftJsonResult(leftValue.Key, leftValue.Value, null));
                differenceCount++;
                leftHasValue = TryReadMetadataRow(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
            }
            else
            {
                if (differenceCount >= offset && drift.Count < limit)
                    drift.Add(new DiffMetadataDriftJsonResult(rightValue.Key, null, rightValue.Value));
                differenceCount++;
                rightHasValue = TryReadMetadataRow(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
            }

        }

        var hasMore = differenceCount > (long)offset + drift.Count;
        return new MetadataRowsDiff(equal, drift, hasMore, differenceCount != drift.Count);
    }

    private static OrderedRowsDiff DiffOrderedRows(
        SqliteConnection leftConnection,
        SqliteConnection rightConnection,
        string leftSql,
        string rightSql,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = leftSql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = rightSql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var onlyInLeft = new List<string>(limit);
        var onlyInRight = new List<string>(limit);
        var leftRowsRead = 0;
        var rightRowsRead = 0;
        var leftHasValue = TryReadRow(leftReader, out var leftValue, ref leftRowsRead, "left", cancellationToken);
        var rightHasValue = TryReadRow(rightReader, out var rightValue, ref rightRowsRead, "right", cancellationToken);
        var equal = true;
        var leftDifferenceCount = 0;
        var rightDifferenceCount = 0;

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
                if (leftDifferenceCount >= offset && onlyInLeft.Count < limit)
                    onlyInLeft.Add(EncodeRow(leftValue.SortValues));
                leftDifferenceCount++;
                leftHasValue = TryReadRow(leftReader, out leftValue, ref leftRowsRead, "left", cancellationToken);
            }
            else
            {
                if (rightDifferenceCount >= offset && onlyInRight.Count < limit)
                    onlyInRight.Add(EncodeRow(rightValue.SortValues));
                rightDifferenceCount++;
                rightHasValue = TryReadRow(rightReader, out rightValue, ref rightRowsRead, "right", cancellationToken);
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
        if (!TableExists(connection, "symbol_reference_candidates")
            || !ColumnExists(connection, "symbol_references", "source_symbol_id")
            || !ColumnExists(connection, "symbol_references", "target_symbol_id")
            || !ColumnExists(connection, "symbol_references", "target_symbol_key")
            || !ColumnExists(connection, "symbol_references", "target_qualifier")
            || !ColumnExists(connection, "symbol_references", "resolution_state")
            || !ColumnExists(connection, "symbol_references", "resolution_candidate_count")
            || !ColumnExists(connection, "symbols", "container_qualified_name"))
        {
            return LegacyReferenceRowsSql;
        }

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

    private static string BuildSymbolRowsSql(SqliteConnection connection)
    {
        var metadataTargetExpr = ColumnExists(connection, "symbols", "is_metadata_target")
            ? "symbols.is_metadata_target"
            : "NULL";
        var metadataTargetSourceExpr = ColumnExists(connection, "symbols", "metadata_target_source")
            ? "symbols.metadata_target_source"
            : "NULL";

        return $$"""
            SELECT
                COALESCE(files.path, ''),
                symbols.kind,
                symbols.sub_kind,
                symbols.name,
                symbols.name_folded,
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

    private static string EncodeRow(object?[] values)
    {
        var fields = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var rawValue = values[i];
            if (rawValue is null or DBNull)
            {
                fields[i] = "-1:";
                continue;
            }

            var value = Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var encodedValue = EncodeFieldValue(value);
            fields[i] = encodedValue.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + encodedValue;
        }

        return string.Join("|", fields);
    }

    private static string EncodeFieldValue(string value)
    {
        if (value.Length <= MaxDiffEncodedFieldSampleLength)
            return value;

        var sample = value[..MaxDiffEncodedFieldSampleLength];
        var hash = HexEncoding.ToLowerHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return sample
            + "...[truncated original_length="
            + value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " sha256="
            + hash
            + "]";
    }

    private sealed record OrderedRowsDiff(
        bool Equal,
        List<string> OnlyInLeft,
        List<string> OnlyInRight,
        bool HasMore,
        bool Omitted);

    private sealed record MetadataRowsDiff(
        bool Equal,
        List<DiffMetadataDriftJsonResult> Drift,
        bool HasMore,
        bool Omitted);

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
    public bool ShowHelp { get; init; }
    public int Limit { get; init; } = 20;
    public int Offset { get; init; }
    public string? ParseError { get; init; }
}
