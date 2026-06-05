using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static class DiffCommandRunner
{
    private const int DefaultDiffLimit = 20;
    internal static int MaxDiffLimit => QueryCommandRunner.NumericFlagUpperBounds["--limit"];
    private const int DriftExitCode = 1;
    private const int SchemaMismatchExitCode = 2;
    private const int UnreadableExitCode = 3;

    public static int Run(string[] args, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(args);
        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
            return CommandExitCodes.Success;
        }

        if (options.ParseError is not null)
            return WriteCommandError(
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
            var leftHeader = ReadHeader(options.LeftDb!);
            var rightHeader = ReadHeader(options.RightDb!);
            if (leftHeader.SchemaVersion != rightHeader.SchemaVersion)
            {
                var schemaMismatch = BuildSchemaMismatchDiff(leftHeader, rightHeader, options);
                WriteResult(schemaMismatch, options, jsonOptions);
                return SchemaMismatchExitCode;
            }

            var result = BuildDiff(leftHeader, rightHeader, options);

            WriteResult(result, options, jsonOptions);

            return result.Identical ? CommandExitCodes.Success : DriftExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            return WriteCommandError(
                options.Json || options.SummaryOnly,
                jsonOptions,
                $"failed to compare databases: {ex.Message}",
                UnreadableExitCode,
                "Pass two readable CodeIndex SQLite database paths.",
                CommandErrorCodes.DbError);
        }
    }

    private static int? ValidateReadableDbFileUri(string dbPath, bool json, JsonSerializerOptions jsonOptions)
    {
        if (SqliteFileUri.TryValidateBounds(dbPath, out var parseError))
            return null;

        return WriteCommandError(
            json,
            jsonOptions,
            $"invalid database file URI: {SqliteFileUri.FormatParseError(parseError)}",
            UnreadableExitCode,
            "Pass two readable CodeIndex SQLite database paths or valid SQLite file URIs.",
            CommandErrorCodes.DbError);
    }

    internal static DiffCommandOptions ParseArgs(string[] args)
    {
        var dbs = new List<string>(2);
        var json = false;
        var detailed = false;
        var summaryOnly = false;
        var limit = DefaultDiffLimit;
        string? parseError = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h":
                    return new DiffCommandOptions { ShowHelp = true };
                case "--json":
                    json = true;
                    break;
                case "--detailed":
                    detailed = true;
                    break;
                case "--summary-only":
                    summaryOnly = true;
                    break;
                case "--limit" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out limit) || limit < 0)
                        parseError = "--limit requires a non-negative integer";
                    else if (limit > MaxDiffLimit)
                    {
                        parseError = $"--limit must be less than or equal to {MaxDiffLimit}";
                        limit = DefaultDiffLimit;
                    }
                    break;
                case "--limit":
                    parseError = "--limit requires a value";
                    break;
                default:
                    if (arg.StartsWith('-'))
                        parseError = $"diff does not support option: '{arg}'";
                    else if (dbs.Count >= 2)
                        parseError = $"diff accepts exactly two database paths; unexpected argument: '{arg}'";
                    else
                        dbs.Add(arg);
                    break;
            }

            if (parseError is not null)
                break;
        }

        if (parseError is null && dbs.Count != 2)
            parseError = "diff requires exactly two database paths";

        return new DiffCommandOptions
        {
            LeftDb = dbs.Count > 0 ? dbs[0] : null,
            RightDb = dbs.Count > 1 ? dbs[1] : null,
            Json = json,
            Detailed = detailed,
            SummaryOnly = summaryOnly,
            Limit = limit,
            ParseError = parseError,
        };
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
            OR key = 'workspace_path_case_sensitive'
            OR key = 'unknown_extension_file_count'
            OR key = 'unknown_extension_file_paths_json'
            OR key = 'unknown_extension_files_truncated'
            OR key = 'unknown_extension_file_path_limit'
            OR key = 'cdidx_writer_version'
        ORDER BY
            key,
            value
        """;

    private const string SymbolRowsSql = """
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
            symbols.is_metadata_target
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
            symbols.is_metadata_target
        """;

    private const string ReferenceRowsSql = """
        SELECT
            COALESCE(files.path, ''),
            symbol_references.symbol_name,
            symbol_references.symbol_name_folded,
            symbol_references.reference_kind,
            symbol_references.line,
            symbol_references.column_number,
            symbol_references.context,
            symbol_references.reference_line_id,
            symbol_references.container_kind,
            symbol_references.container_name,
            symbol_references.container_name_folded
        FROM symbol_references
        LEFT JOIN files ON files.id = symbol_references.file_id
        ORDER BY
            COALESCE(files.path, ''),
            symbol_references.symbol_name,
            symbol_references.symbol_name_folded,
            symbol_references.reference_kind,
            symbol_references.line,
            symbol_references.column_number,
            symbol_references.context,
            symbol_references.reference_line_id,
            symbol_references.container_kind,
            symbol_references.container_name,
            symbol_references.container_name_folded
        """;

    private static DiffJsonResult BuildDiff(DiffDbHeader left, DiffDbHeader right, DiffCommandOptions options)
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
        var identical =
            summary.SchemaVersionsEqual &&
            summary.FileCountDelta == 0 &&
            summary.SymbolCountDelta == 0 &&
            summary.ReferenceCountDelta == 0;

        using var leftConnection = OpenReadOnlyConnection(options.LeftDb!);
        using var rightConnection = OpenReadOnlyConnection(options.RightDb!);

        if (!options.SummaryOnly)
        {
            var fileDiff = DiffOrderedStrings(leftConnection, rightConnection, FilePathRowsSql, options.Limit);
            filesOnlyInLeft = fileDiff.OnlyInLeft;
            filesOnlyInRight = fileDiff.OnlyInRight;
            identical = identical && fileDiff.Equal;
        }

        if (options.Detailed)
        {
            var symbolDiff = DiffOrderedRows(leftConnection, rightConnection, SymbolRowsSql, options.Limit);
            symbolsOnlyInLeft = symbolDiff.OnlyInLeft;
            symbolsOnlyInRight = symbolDiff.OnlyInRight;
            identical = identical && symbolDiff.Equal;
        }

        if (identical)
        {
            identical =
                RowsEqual(leftConnection, rightConnection, FileRowsSql) &&
                RowsEqual(leftConnection, rightConnection, ChunkRowsSql) &&
                RowsEqual(leftConnection, rightConnection, ReferenceLineRowsSql) &&
                RowsEqual(leftConnection, rightConnection, FileIssueRowsSql) &&
                RowsEqual(leftConnection, rightConnection, MetaRowsSql) &&
                (options.Detailed || RowsEqual(leftConnection, rightConnection, SymbolRowsSql)) &&
                RowsEqual(leftConnection, rightConnection, ReferenceRowsSql);
        }

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
            options.Limit,
            options.Detailed);
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
            options.Limit,
            options.Detailed);
    }

    private static OrderedRowsDiff DiffOrderedRows(SqliteConnection leftConnection, SqliteConnection rightConnection, string sql, int limit)
    {
        if (limit == 0)
            return new OrderedRowsDiff(RowsEqual(leftConnection, rightConnection, sql), [], []);

        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = sql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = sql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var onlyInLeft = new List<string>(limit);
        var onlyInRight = new List<string>(limit);
        var leftHasValue = TryReadEncodedRow(leftReader, out var leftValue);
        var rightHasValue = TryReadEncodedRow(rightReader, out var rightValue);
        var equal = true;

        while (leftHasValue || rightHasValue)
        {
            var comparison = leftHasValue && rightHasValue
                ? CompareRows(leftValue, rightValue)
                : leftHasValue ? -1 : 1;

            if (comparison == 0)
            {
                leftHasValue = TryReadEncodedRow(leftReader, out leftValue);
                rightHasValue = TryReadEncodedRow(rightReader, out rightValue);
                continue;
            }

            equal = false;
            if (comparison < 0)
            {
                if (onlyInLeft.Count < limit)
                    onlyInLeft.Add(leftValue.Encoded);
                leftHasValue = TryReadEncodedRow(leftReader, out leftValue);
            }
            else
            {
                if (onlyInRight.Count < limit)
                    onlyInRight.Add(rightValue.Encoded);
                rightHasValue = TryReadEncodedRow(rightReader, out rightValue);
            }

            if (onlyInLeft.Count >= limit && onlyInRight.Count >= limit)
                break;
        }

        return new OrderedRowsDiff(equal, onlyInLeft, onlyInRight);
    }

    private static OrderedRowsDiff DiffOrderedStrings(SqliteConnection leftConnection, SqliteConnection rightConnection, string sql, int limit)
    {
        if (limit == 0)
            return new OrderedRowsDiff(StringRowsEqual(leftConnection, rightConnection, sql), [], []);

        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = sql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = sql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var onlyInLeft = new List<string>(limit);
        var onlyInRight = new List<string>(limit);
        var leftHasValue = TryReadString(leftReader, out var leftValue);
        var rightHasValue = TryReadString(rightReader, out var rightValue);
        var equal = true;

        while (leftHasValue || rightHasValue)
        {
            var comparison = leftHasValue && rightHasValue
                ? string.CompareOrdinal(leftValue, rightValue)
                : leftHasValue ? -1 : 1;

            if (comparison == 0)
            {
                leftHasValue = TryReadString(leftReader, out leftValue);
                rightHasValue = TryReadString(rightReader, out rightValue);
                continue;
            }

            equal = false;
            if (comparison < 0)
            {
                if (onlyInLeft.Count < limit)
                    onlyInLeft.Add(leftValue);
                leftHasValue = TryReadString(leftReader, out leftValue);
            }
            else
            {
                if (onlyInRight.Count < limit)
                    onlyInRight.Add(rightValue);
                rightHasValue = TryReadString(rightReader, out rightValue);
            }

            if (onlyInLeft.Count >= limit && onlyInRight.Count >= limit)
                break;
        }

        return new OrderedRowsDiff(equal, onlyInLeft, onlyInRight);
    }

    private static bool RowsEqual(SqliteConnection leftConnection, SqliteConnection rightConnection, string sql)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = sql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = sql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var leftHasValue = TryReadEncodedRow(leftReader, out var leftValue);
        var rightHasValue = TryReadEncodedRow(rightReader, out var rightValue);
        while (leftHasValue && rightHasValue)
        {
            if (!string.Equals(leftValue.Encoded, rightValue.Encoded, StringComparison.Ordinal))
                return false;
            leftHasValue = TryReadEncodedRow(leftReader, out leftValue);
            rightHasValue = TryReadEncodedRow(rightReader, out rightValue);
        }

        return leftHasValue == rightHasValue;
    }

    private static bool StringRowsEqual(SqliteConnection leftConnection, SqliteConnection rightConnection, string sql)
    {
        using var leftCommand = leftConnection.CreateCommand();
        leftCommand.CommandText = sql;
        using var rightCommand = rightConnection.CreateCommand();
        rightCommand.CommandText = sql;
        using var leftReader = leftCommand.ExecuteReader();
        using var rightReader = rightCommand.ExecuteReader();

        var leftHasValue = TryReadString(leftReader, out var leftValue);
        var rightHasValue = TryReadString(rightReader, out var rightValue);
        while (leftHasValue && rightHasValue)
        {
            if (!string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                return false;
            leftHasValue = TryReadString(leftReader, out leftValue);
            rightHasValue = TryReadString(rightReader, out rightValue);
        }

        return leftHasValue == rightHasValue;
    }

    private static bool TryReadEncodedRow(SqliteDataReader reader, out EncodedDiffRow value)
    {
        if (!reader.Read())
        {
            value = EncodedDiffRow.Empty;
            return false;
        }

        var sortValues = new object?[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            sortValues[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);

        value = new EncodedDiffRow(EncodeRow(reader), sortValues);
        return true;
    }

    private static int CompareRows(EncodedDiffRow left, EncodedDiffRow right)
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

    private static bool TryReadString(SqliteDataReader reader, out string value)
    {
        if (!reader.Read())
        {
            value = string.Empty;
            return false;
        }

        value = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        return true;
    }

    private static SqliteConnection OpenReadOnlyConnection(string dbPath)
    {
        var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(LongPath.EnsureWindowsPrefix(dbPath)))
            throw new IOException($"database not found: {dbPath}");

        var connectionString = DbPathResolver.BuildSqliteConnectionString(dbPath, SqliteOpenMode.ReadOnly);

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static long ExecuteLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DiffDbHeader ReadHeader(string dbPath)
    {
        var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        using var connection = OpenReadOnlyConnection(dbPath);

        return new DiffDbHeader(
            Path.GetFullPath(isUri ? dbPath : dbPath),
            ExecuteLong(connection, "PRAGMA user_version"),
            ExecuteCountIfTableExists(connection, "files"),
            ExecuteCountIfTableExists(connection, "symbols"),
            ExecuteCountIfTableExists(connection, "symbol_references"));
    }

    private static long ExecuteCountIfTableExists(SqliteConnection connection, string table)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
            exists.Parameters.AddWithValue("$table", table);
            if (exists.ExecuteScalar() is null)
                return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string EncodeRow(SqliteDataReader reader)
    {
        var fields = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.IsDBNull(i))
            {
                fields[i] = "-1:";
                continue;
            }

            var value = Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            fields[i] = value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
        }

        return string.Join("|", fields);
    }

    private static void WriteJson(DiffJsonResult result, JsonSerializerOptions jsonOptions)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            result,
            CliJsonSerializerContextFactory.Create(jsonOptions).DiffJsonResult));
    }

    private static void WriteSummaryJson(DiffJsonResult result, JsonSerializerOptions jsonOptions)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new DiffSummaryOnlyJsonResult(result.Status, result.Identical, result.LeftDb, result.RightDb, result.Summary),
            CliJsonSerializerContextFactory.Create(jsonOptions).DiffSummaryOnlyJsonResult));
    }

    private static void WriteResult(DiffJsonResult result, DiffCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (options.SummaryOnly)
            WriteSummaryJson(result, jsonOptions);
        else if (options.Json)
            WriteJson(result, jsonOptions);
        else
            WriteText(result, options);
    }

    private static void WriteText(DiffJsonResult result, DiffCommandOptions options)
    {
        Console.WriteLine("Index database diff");
        Console.WriteLine($"  left   : {result.LeftDb}");
        Console.WriteLine($"  right  : {result.RightDb}");
        Console.WriteLine($"  status : {result.Status}");
        Console.WriteLine($"  schema : {result.Summary.LeftSchemaVersion} -> {result.Summary.RightSchemaVersion}");
        Console.WriteLine($"  files  : {result.Summary.LeftFileCount} -> {result.Summary.RightFileCount} ({FormatDelta(result.Summary.FileCountDelta)})");
        Console.WriteLine($"  symbols: {result.Summary.LeftSymbolCount} -> {result.Summary.RightSymbolCount} ({FormatDelta(result.Summary.SymbolCountDelta)})");
        Console.WriteLine($"  refs   : {result.Summary.LeftReferenceCount} -> {result.Summary.RightReferenceCount} ({FormatDelta(result.Summary.ReferenceCountDelta)})");

        WriteList("files only in left", result.FilesOnlyInLeft);
        WriteList("files only in right", result.FilesOnlyInRight);
        if (options.Detailed)
        {
            WriteList("symbols only in left", result.SymbolsOnlyInLeft ?? []);
            WriteList("symbols only in right", result.SymbolsOnlyInRight ?? []);
        }
    }

    private static void WriteList(string label, List<string> values)
    {
        if (values.Count == 0)
            return;
        Console.WriteLine($"  {label}:");
        foreach (var value in values)
            Console.WriteLine($"    - {value}");
    }

    private static string FormatDelta(long delta) => delta >= 0 ? $"+{delta}" : delta.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null, string? errorCode = null)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new CommandErrorJsonResult("error", message, hint, errorCode),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult));
        else
        {
            Console.Error.WriteLine($"Error [{errorCode ?? CommandErrorCodes.UsageError}]: {message}");
            if (!string.IsNullOrWhiteSpace(hint))
                Console.Error.WriteLine($"Hint: {hint}");
        }
        return exitCode;
    }

    private sealed record OrderedRowsDiff(
        bool Equal,
        List<string> OnlyInLeft,
        List<string> OnlyInRight);

    private sealed record EncodedDiffRow(
        string Encoded,
        object?[] SortValues)
    {
        public static readonly EncodedDiffRow Empty = new(string.Empty, []);
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
    public string? ParseError { get; init; }
}
