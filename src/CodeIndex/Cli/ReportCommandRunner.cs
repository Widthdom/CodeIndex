using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodeIndex.Diagnostics;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs `cdidx report --output <path>`, which bundles a redacted crash-repro
/// tarball (`.tar.gz`) containing the cdidx version, OS / .NET runtime info,
/// the schema table list with row counts, and recent lifecycle log lines.
/// User-content fields (paths, query strings, args) are redacted by default.
/// `cdidx report --output <path>` を実行する。バージョン、OS / .NET ランタイム情報、
/// スキーマのテーブル一覧と行数、最近のライフサイクルログを含む匿名化済み
/// tarball (`.tar.gz`) を作る。パス・クエリ文字列・args 等のユーザコンテンツは
/// 既定で伏字化する。
/// </summary>
public static class ReportCommandRunner
{
    internal const int DefaultLogLines = 200;
    internal const int MaxLogLines = 2000;
    internal const int MaxLogFileTailBytes = 1024 * 1024;
    internal const int MaxLogTailLineChars = 16 * 1024;
    internal const int MaxRecentLogFiles = 32;
    internal const int MaxSchemaTables = 64;
    internal const int MaxSchemaTableNameDisplayChars = 96;
    internal const int MaxSchemaRowCountScanRows = 1000;
    internal const string RedactedPlaceholder = "[redacted]";
    internal const string BundleArtifactFormat = "tar.gz";
    internal const string BundleArtifactMediaType = "application/gzip";
    internal const bool JsonMetadataStdoutOnly = true;
    internal const UnixFileMode BundleFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal static readonly DateTimeOffset BundleEntryModificationTime = DateTimeOffset.UnixEpoch;
    private const string SchemaTableLabelPrefix = "table_";
    private const int SupportManifestVersion = 2;

    public static int Run(string[] cmdArgs, JsonSerializerOptions jsonOptions, string? appVersion = null)
    {
        var options = ParseArgs(cmdArgs);
        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
            return CommandExitCodes.Success;
        }

        if (options.ParseError != null)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Run `cdidx report --help` to see the supported command shape.",
                CommandErrorCodes.UsageError);

        if (string.IsNullOrWhiteSpace(options.OutputPath))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "report requires --output <path>",
                CommandExitCodes.UsageError,
                "Pass `--output report.tgz` (or another writable path) to choose where the redacted bundle is written.",
                CommandErrorCodes.UsageError);

        try
        {
            var fullOutputPath = Path.GetFullPath(options.OutputPath!);
            var outputExtensionWarning = GetOutputExtensionWarning(fullOutputPath);
            var resolvedVersion = appVersion ?? ConsoleUi.LoadVersion();
            var bundle = BuildBundle(options, resolvedVersion);
            WriteBundle(fullOutputPath, bundle);
            if (outputExtensionWarning is not null)
                CommandErrorWriter.WriteWarning(outputExtensionWarning);

            var summary = new ReportBundleSummary(
                options.Json ? RedactedPlaceholder : fullOutputPath,
                resolvedVersion,
                BundleArtifactFormat,
                BundleArtifactMediaType,
                RecommendedBundleExtensions(),
                JsonMetadataStdoutOnly,
                outputExtensionWarning is null ? [] : [outputExtensionWarning],
                bundle.Files.Count,
                bundle.SchemaTables.Count,
                bundle.LogLinesIncluded,
                bundle.LogIncluded,
                bundle.DbIncluded,
                options.Json ? RedactLocalJsonPath(bundle.DbPath) : bundle.DbPath);

            if (options.Json)
            {
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                Console.WriteLine(JsonSerializer.Serialize(summary, jsonContext.ReportBundleSummary));
            }
            else
            {
                Console.WriteLine("Bug report bundle");
                Console.WriteLine($"  output       : {fullOutputPath}");
                Console.WriteLine($"  format       : {BundleArtifactFormat} ({string.Join(", ", RecommendedBundleExtensions())})");
                Console.WriteLine($"  cdidx        : v{summary.Version}");
                Console.WriteLine($"  files        : {summary.Files}");
                Console.WriteLine($"  schema rows  : {summary.SchemaTables}");
                Console.WriteLine($"  log lines    : {(summary.LogIncluded ? summary.LogLinesIncluded.ToString() : "skipped")}");
                Console.WriteLine($"  schema source: {(summary.DbIncluded ? bundle.DbPath : "(no DB found)")}");
                Console.WriteLine();
                Console.WriteLine("Attach the tarball to the GitHub issue. Path lists, query strings, and");
                Console.WriteLine("`args=` log lines are redacted by default; rerun with `--include-args` to");
                Console.WriteLine("include literal command-line arguments only when you trust the recipient.");
            }
            return CommandExitCodes.Success;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to build report: {FormatReportExceptionMessage(ex)}",
                CommandExitCodes.DatabaseError,
                "Retry `cdidx report --output <path>`. If this persists, check that the output directory is writable.",
                CommandErrorCodes.DbError);
        }
    }

    internal static string? GetOutputExtensionWarning(string outputPath)
    {
        var fileName = Path.GetFileName(outputPath);
        if (string.IsNullOrWhiteSpace(fileName) || HasRecommendedBundleExtension(fileName))
            return null;

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
            return null;

        var extensionLabel = extension.TrimStart('.').ToUpperInvariant();
        return $"report --output writes a gzip-compressed tar report bundle ({BundleArtifactFormat}), not {extensionLabel} output; use .tgz or .tar.gz. --json only changes stdout metadata.";
    }

    private static bool HasRecommendedBundleExtension(string fileName) =>
        fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    internal static List<string> RecommendedBundleExtensions() => [".tgz", ".tar.gz"];

    private static string FormatReportExceptionMessage(Exception ex) =>
        DiagnosticRedactor.FormatExceptionMessage(ex, maxChars: 512);

    private static string? RedactLocalJsonPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? path : RedactedPlaceholder;

    internal static ReportBundle BuildBundle(
        ReportCommandOptions options,
        string version,
        DateTimeOffset? generatedAtUtcForTesting = null)
    {
        var nowUtc = generatedAtUtcForTesting ?? DateTimeOffset.UtcNow;
        var bundle = new ReportBundle
        {
            GeneratedAtUtc = nowUtc,
        };

        var metadata = new ReportMetadata(
            Version: version,
            GeneratedAtUtc: nowUtc.ToString("O"),
            DotNetRuntimeVersion: Environment.Version.ToString(),
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            OsDescription: RuntimeInformation.OSDescription,
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            IsLittleEndian: BitConverter.IsLittleEndian);
        var metaJson = JsonSerializer.Serialize(metadata, ReportMetadataJsonContext.Default.ReportMetadata);
        bundle.AddText("metadata.json", metaJson);

        bundle.AddText("version.txt", $"cdidx v{version}\n");
        bundle.AddText(
            "env.txt",
            string.Join('\n',
                $"cdidx-version: {version}",
                $"generated-at-utc: {nowUtc:O}",
                $"dotnet-runtime: {Environment.Version}",
                $"framework: {RuntimeInformation.FrameworkDescription}",
                $"os: {RuntimeInformation.OSDescription}",
                $"os-architecture: {RuntimeInformation.OSArchitecture}",
                $"process-architecture: {RuntimeInformation.ProcessArchitecture}",
                "") + "\n");

        var (schemaText, tables, dbPath, dbIncluded, schemaTablesTruncated) = BuildSchemaSummary(options.DbPath);
        bundle.SchemaTables = tables;
        bundle.SchemaTablesTruncated = schemaTablesTruncated;
        bundle.DbIncluded = dbIncluded;
        bundle.DbPath = dbPath;
        bundle.AddText("schema.txt", schemaText);

        bundle.LogIncluded = options.IncludeLog;
        bundle.LogLinesIncluded = 0;
        var redactions = ReportRedactionSummary.Empty;
        if (options.IncludeLog && options.LogLines > 0)
        {
            var logText = BuildRecentLogTail(
                options.LogLines,
                options.IncludeArgs,
                out var linesIncluded,
                out redactions,
                out var logTailTruncated,
                out var logLineCharsTruncated);
            bundle.LogLinesIncluded = linesIncluded;
            bundle.LogTailTruncated = logTailTruncated;
            bundle.LogLineCharsTruncated = logLineCharsTruncated;
            bundle.AddText("log/stderr-recent.log", logText);
        }

        var supportManifest = BuildSupportManifest(options, bundle, nowUtc, redactions);
        bundle.AddText(
            "support-manifest.json",
            JsonSerializer.Serialize(supportManifest, ReportSupportManifestJsonContext.Default.ReportSupportManifest));
        bundle.AddText("README.md", BuildReadme(version, options.IncludeLog, options.IncludeArgs));
        return bundle;
    }

    internal static string BuildReadme(string version, bool includeLog, bool includeArgs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# cdidx bug-report bundle");
        sb.AppendLine();
        sb.AppendLine($"Generated by cdidx v{version}. Attach this tarball to the GitHub issue you are filing at");
        sb.AppendLine("<https://github.com/Widthdom/CodeIndex/issues>.");
        sb.AppendLine();
        sb.AppendLine("The `--output` file is always a gzip-compressed tar report bundle (`.tgz` or `.tar.gz`).");
        sb.AppendLine("`--json` only changes the command summary written to stdout; it does not make the artifact JSON.");
        sb.AppendLine();
        sb.AppendLine("## Contents");
        sb.AppendLine();
        sb.AppendLine("- `metadata.json` — version, OS, .NET runtime info (machine-readable).");
        sb.AppendLine("- `version.txt` — cdidx version only.");
        sb.AppendLine("- `env.txt` — human-readable OS / runtime summary.");
        sb.AppendLine("- `schema.txt` — capped SQLite table labels and bounded row counts (no raw table names or row contents).");
        sb.AppendLine("- `support-manifest.json` — machine-readable redaction, omission, readiness, and diagnostic summary.");
        sb.AppendLine("- Archive entry modification times are fixed for reproducible tar metadata; generation time is recorded in `metadata.json`, `env.txt`, and `support-manifest.json`.");
        if (includeLog)
        {
            sb.AppendLine(
                "- `log/stderr-recent.log` — up to the requested last N lifecycle-log lines, " +
                $"selected from the {MaxRecentLogFiles} newest log files, with each line capped at {MaxLogTailLineChars} characters");
            sb.AppendLine(includeArgs
                ? "  (includes literal `args=` lines; path-bearing lifecycle fields stay redacted)."
                : "  (`args=` and path-bearing lifecycle fields are redacted; rerun with `--include-args` to keep arguments literal).");
        }
        else
        {
            sb.AppendLine("- (log skipped via `--no-log`)");
        }
        sb.AppendLine();
        sb.AppendLine("## Redactions");
        sb.AppendLine();
        sb.AppendLine("- Indexed source content, file paths, query strings, and `args=` lines are not included by default.");
        sb.AppendLine("- Path-bearing lifecycle fields such as `process_path=`, `base_dir=`, `cwd=`, `db=`, and `path=` are redacted by default.");
        sb.AppendLine($"- Schema reporting emits at most {MaxSchemaTables} generated table labels, capped at {MaxSchemaTableNameDisplayChars} display characters, with raw table names omitted and row counts bounded at {MaxSchemaRowCountScanRows} scanned rows per table.");
        return sb.ToString();
    }

    internal static (
        string Text,
        List<ReportSchemaTable> Tables,
        string? DbPath,
        bool DbIncluded,
        bool TablesTruncated) BuildSchemaSummary(string dbPath)
    {
        var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
        if (!File.Exists(LongPath.EnsureWindowsPrefix(normalizedDbPath)))
        {
            var missingText = $"no SQLite index found at: {RedactedPlaceholder}\nRun `cdidx index <projectPath>` first if you want schema details attached.\n";
            return (missingText, new List<ReportSchemaTable>(), normalizedDbPath, false, false);
        }

        var tables = new List<ReportSchemaTable>();
        var connectionString = SqliteConnectionPolicy.BuildConnectionString(normalizedDbPath, SqliteConnectionPolicyMode.ReadOnly);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var tableNames = new List<string>();
        using (var listCmd = SqliteConnectionPolicy.CreateCommand(connection))
        {
            listCmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' LIMIT {MaxSchemaTables + 1}";
            using var reader = listCmd.ExecuteReader();
            while (reader.Read())
                tableNames.Add(reader.GetString(0));
        }

        var tableListTruncated = tableNames.Count > MaxSchemaTables;
        if (tableListTruncated)
            tableNames.RemoveRange(MaxSchemaTables, tableNames.Count - MaxSchemaTables);

        for (var i = 0; i < tableNames.Count; i++)
        {
            var name = tableNames[i];
            long rowCount;
            var rowCountTruncated = false;
            try
            {
                using var countCmd = SqliteConnectionPolicy.CreateCommand(connection);
                countCmd.CommandText = SqliteCommandPolicy.CountRowsWithLimitSql(name, "$limit");
                SqliteCommandPolicy.AddLimit(countCmd, "$limit", MaxSchemaRowCountScanRows + 1);
                var cappedCount = SqliteCommandPolicy.ReadInt64Scalar(countCmd, $"report schema row count {name}");
                rowCountTruncated = cappedCount > MaxSchemaRowCountScanRows;
                rowCount = rowCountTruncated ? MaxSchemaRowCountScanRows : cappedCount;
            }
            catch (SqliteException)
            {
                rowCount = -1;
            }
            tables.Add(new ReportSchemaTable(FormatSchemaTableLabel(i), rowCount, rowCountTruncated));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"database: {RedactedPlaceholder}");
        sb.AppendLine(tableListTruncated
            ? $"tables  : {tables.Count} (capped; additional tables omitted)"
            : $"tables  : {tables.Count}");
        sb.AppendLine($"limits  : table entries <= {MaxSchemaTables}, table label chars <= {MaxSchemaTableNameDisplayChars}, row count scan rows <= {MaxSchemaRowCountScanRows}");
        sb.AppendLine("privacy : raw table names redacted; generated labels are used");
        sb.AppendLine();
        sb.AppendLine("label | row_count");
        sb.AppendLine("------|----------");
        foreach (var t in tables)
            sb.AppendLine($"{t.Name} | {FormatSchemaRowCount(t)}");

        return (sb.ToString(), tables, normalizedDbPath, true, tableListTruncated);
    }

    private static string FormatSchemaTableLabel(int index) =>
        SchemaTableLabelPrefix + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatSchemaRowCount(ReportSchemaTable table)
    {
        if (table.RowCount < 0)
            return "(unreadable)";

        return table.RowCountTruncated ? $">={table.RowCount}" : table.RowCount.ToString();
    }

    internal static string BuildRecentLogTail(int maxLines, bool includeArgs, out int linesIncluded)
        => BuildRecentLogTail(maxLines, includeArgs, out linesIncluded, out _);

    internal static string BuildRecentLogTail(
        int maxLines,
        bool includeArgs,
        out int linesIncluded,
        out ReportRedactionSummary redactions)
        => BuildRecentLogTail(maxLines, includeArgs, out linesIncluded, out redactions, out _);

    internal static string BuildRecentLogTail(
        int maxLines,
        bool includeArgs,
        out int linesIncluded,
        out ReportRedactionSummary redactions,
        out bool logTailTruncated)
        => BuildRecentLogTail(maxLines, includeArgs, out linesIncluded, out redactions, out logTailTruncated, out _);

    internal static string BuildRecentLogTail(
        int maxLines,
        bool includeArgs,
        out int linesIncluded,
        out ReportRedactionSummary redactions,
        out bool logTailTruncated,
        out bool logLineCharsTruncated)
        => ReportLogTailBuilder.BuildRecentLogTail(
            maxLines,
            includeArgs,
            out linesIncluded,
            out redactions,
            out logTailTruncated,
            out logLineCharsTruncated);

    internal static ReportSupportManifest BuildSupportManifest(
        ReportCommandOptions options,
        ReportBundle bundle,
        DateTimeOffset generatedAtUtc,
        ReportRedactionSummary redactions)
    {
        var readiness = BuildReadinessSnapshot(bundle.DbPath, bundle.DbIncluded);
        var diagnostics = BuildDiagnosticSummary();
        var omissions = BuildOmissionSummary(options, bundle, readiness);
        return new ReportSupportManifest(
            ManifestVersion: SupportManifestVersion,
            GeneratedAtUtc: generatedAtUtc.ToString("O"),
            Artifact: new ReportManifestArtifact(
                BundleArtifactFormat,
                BundleArtifactMediaType,
                RecommendedBundleExtensions(),
                JsonMetadataStdoutOnly),
            Limits: new ReportManifestLimits(
                MaxSchemaTables,
                MaxSchemaTableNameDisplayChars,
                MaxSchemaRowCountScanRows,
                MaxLogLines,
                MaxLogFileTailBytes,
                MaxLogTailLineChars,
                MaxRecentLogFiles),
            Bundle: new ReportManifestBundle(
                DbIncluded: bundle.DbIncluded,
                LogIncluded: bundle.LogIncluded,
                IncludeArgs: options.IncludeArgs,
                Files: bundle.Files.Count + 2,
                SchemaTables: bundle.SchemaTables.Count,
                LogLinesIncluded: bundle.LogLinesIncluded),
            Redactions: redactions,
            Omissions: omissions,
            Readiness: readiness,
            Diagnostics: diagnostics);
    }

    private static ReportManifestOmissions BuildOmissionSummary(
        ReportCommandOptions options,
        ReportBundle bundle,
        ReportReadinessSnapshot readiness)
    {
        var schema = new List<string> { "database_path", "raw_schema_table_names", "table_row_contents" };
        if (!bundle.DbIncluded)
            schema.Add("schema_unavailable_no_database");
        if (bundle.SchemaTablesTruncated)
            schema.Add("schema_tables_after_limit");
        if (bundle.SchemaTables.Any(static t => t.RowCountTruncated))
            schema.Add("row_counts_after_scan_limit");

        var log = new List<string> { "log_source_directory" };
        if (!options.IncludeLog)
        {
            log.Add("lifecycle_log_skipped_by_option");
        }
        else
        {
            if (bundle.LogLinesIncluded == 0)
                log.Add("lifecycle_log_unavailable_or_empty");
            if (bundle.LogTailTruncated)
                log.Add("older_log_lines_outside_tail_limit");
            if (bundle.LogLineCharsTruncated)
                log.Add("log_line_chars_after_limit");
        }
        if (!options.IncludeArgs)
            log.Add("literal_args");

        var status = new List<string>();
        if (readiness.Source != "database")
            status.Add("readiness_snapshot_unavailable");
        status.Add("raw_config_values");
        status.Add("diagnostic_paths");

        return new ReportManifestOmissions(schema, log, status);
    }

    private static ReportReadinessSnapshot BuildReadinessSnapshot(string? dbPath, bool dbIncluded)
    {
        if (!dbIncluded || string.IsNullOrWhiteSpace(dbPath))
        {
            return ReportReadinessSnapshot.Unavailable(
                "database_unavailable",
                ["readiness_unavailable"]);
        }

        try
        {
            var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
            var connectionString = SqliteConnectionPolicy.BuildConnectionString(normalizedDbPath, SqliteConnectionPolicyMode.ReadOnly);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var tables = LoadTableNames(connection);
            var symbolColumns = LoadColumnNames(connection, "symbols");
            var meta = tables.Contains("codeindex_meta")
                ? LoadCodeIndexMeta(connection)
                : new Dictionary<string, string?>(StringComparer.Ordinal);
            var userVersion = ReadUserVersion(connection);

            var graphTableAvailable = tables.Contains("symbol_references");
            var issuesTableAvailable = tables.Contains("file_issues");
            var fileIssuesDataCurrent = issuesTableAvailable && (userVersion & DbContext.IssuesReadyFlag) == DbContext.IssuesReadyFlag;
            var migrationInProgress = string.Equals(GetMeta(meta, DbContext.BatchInProgressMetaKey), "true", StringComparison.OrdinalIgnoreCase);
            var hasCSharpFiles = CountFilesByLanguage(connection, "csharp") > 0;
            var hasSqlFiles = CountFilesByLanguage(connection, "sql") > 0;
            var csharpSymbolNameReady = !hasCSharpFiles || string.Equals(
                GetMeta(meta, DbContext.CSharpSymbolNameContractVersionMetaKey),
                DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
            var csharpMetadataTargetReady = !hasCSharpFiles
                || (symbolColumns.Contains("is_metadata_target")
                    && symbolColumns.Contains("metadata_target_source")
                    && string.Equals(
                        GetMeta(meta, DbContext.GetMetadataTargetVersionMetaKey("csharp")),
                        DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal));
            var sqlGraphContractReady = !hasSqlFiles || string.Equals(
                GetMeta(meta, DbContext.SqlGraphContractVersionMetaKey),
                DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
            var hotspotFamilyReady = string.Equals(
                GetMeta(meta, DbContext.HotspotFamilyVersionMetaKey),
                DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
            var foldReady = (userVersion & DbContext.FoldReadyFlag) == DbContext.FoldReadyFlag
                && symbolColumns.Contains("name_folded");

            var degradedFields = new List<string>();
            AddIfFalse(degradedFields, graphTableAvailable, "graph_table_available");
            AddIfFalse(degradedFields, issuesTableAvailable, "issues_table_available");
            AddIfFalse(degradedFields, fileIssuesDataCurrent, "file_issues_data_current");
            if (migrationInProgress)
                degradedFields.Add("migration_in_progress");
            AddIfFalse(degradedFields, csharpSymbolNameReady, "csharp_symbol_name_ready");
            AddIfFalse(degradedFields, csharpMetadataTargetReady, "csharp_metadata_target_ready");
            AddIfFalse(degradedFields, sqlGraphContractReady, "sql_graph_contract_ready");
            AddIfFalse(degradedFields, hotspotFamilyReady, "hotspot_family_ready");
            AddIfFalse(degradedFields, foldReady, "fold_ready");

            return new ReportReadinessSnapshot(
                Source: "database",
                UnavailableReason: null,
                GraphTableAvailable: graphTableAvailable,
                IssuesTableAvailable: issuesTableAvailable,
                FileIssuesDataCurrent: fileIssuesDataCurrent,
                MigrationInProgress: migrationInProgress,
                CSharpSymbolNameReady: csharpSymbolNameReady,
                CSharpMetadataTargetReady: csharpMetadataTargetReady,
                SqlGraphContractReady: sqlGraphContractReady,
                HotspotFamilyReady: hotspotFamilyReady,
                FoldReady: foldReady,
                DegradedFields: degradedFields.Count == 0 ? null : degradedFields);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ReportReadinessSnapshot.Unavailable(
                "read_failed",
                ["readiness_unavailable"]);
        }
    }

    private static ReportDiagnosticSummary BuildDiagnosticSummary()
    {
        var extractorStatus = ExtractorPluginRegistry.GetStatusSnapshot();
        var hookSnapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();
        return new ReportDiagnosticSummary(
            Extractors: new ReportExtractorDiagnosticSummary(
                extractorStatus.PluginAssemblyCount,
                extractorStatus.PatternConfigCount,
                extractorStatus.SymbolExtractorCount,
                extractorStatus.ReferenceExtractorCount,
                extractorStatus.SkippedFileCount,
                extractorStatus.DiagnosticCount,
                extractorStatus.DiagnosticsTruncated),
            Hooks: new ReportHookDiagnosticSummary(
                hookSnapshot.Hooks.Count,
                hookSnapshot.Diagnostics.Count,
                (long)Math.Ceiling(hookSnapshot.CallbackBudget.TotalMilliseconds)),
            Config: new ReportConfigDiagnosticSummary(
                RawValuesIncluded: false,
                Notes: ["raw_config_values_not_collected"]));
    }

    private static HashSet<string> LoadTableNames(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static HashSet<string> LoadColumnNames(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        if (!LoadTableNames(connection).Contains(tableName))
            return columns;
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(tableName);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static Dictionary<string, string?> LoadCodeIndexMeta(SqliteConnection connection)
    {
        var meta = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.CommandText = "SELECT key, value FROM codeindex_meta";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            meta[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return meta;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long CountFilesByLanguage(SqliteConnection connection, string lang)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(connection);
        cmd.CommandText = "SELECT COUNT(*) FROM files WHERE lang = $lang";
        SqliteCommandPolicy.AddText(cmd, "$lang", lang);
        return SqliteCommandPolicy.ReadInt64Scalar(cmd, $"report language file count {lang}");
    }

    private static string? GetMeta(Dictionary<string, string?> meta, string key)
        => meta.TryGetValue(key, out var value) ? value : null;

    private static void AddIfFalse(List<string> degradedFields, bool value, string field)
    {
        if (!value)
            degradedFields.Add(field);
    }

    internal static IReadOnlyList<string> ReadLogFileTailLines(string path, int maxLines)
        => ReportLogTailBuilder.ReadLogFileTailLines(path, maxLines);

    internal static string RedactSensitiveFields(string line)
    {
        return RedactLogLine(line, includeArgs: false);
    }

    internal static string RedactLogLine(string line, bool includeArgs) =>
        DiagnosticRedactor.RedactReportLogLine(line, includeArgs, RedactedPlaceholder);

    internal static void WriteBundle(string outputPath, ReportBundle bundle, Action? beforeWriteEntries = null)
        => ReportBundleWriter.Write(outputPath, bundle, beforeWriteEntries);

    internal static ReportCommandOptions ParseArgs(string[] args)
    {
        var options = new ReportCommandOptions
        {
            DbPath = Path.Combine(".cdidx", "codeindex.db"),
            LogLines = DefaultLogLines,
            IncludeLog = true,
            IncludeArgs = false,
        };

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    options.DbPath = args[++i];
                    break;
                case "--db":
                    options.ParseError = "--db requires a value";
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    options.OutputPath = args[++i];
                    break;
                case "--output" or "-o":
                    options.ParseError = "--output requires a value";
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--no-log":
                    options.IncludeLog = false;
                    break;
                case "--include-args":
                    options.IncludeArgs = true;
                    break;
                case "--log-lines" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedLines) || parsedLines < 0)
                        options.ParseError = $"--log-lines requires a non-negative integer, got '{args[i]}'";
                    else
                        options.LogLines = Math.Min(parsedLines, MaxLogLines);
                    break;
                case "--log-lines":
                    options.ParseError = "--log-lines requires a value";
                    break;
                case "--help" or "-h":
                    options.ShowHelp = true;
                    return options;
                default:
                    if (args[i].StartsWith('-'))
                        options.ParseError = $"report does not support option: '{args[i]}'";
                    else
                        options.ParseError = $"report does not accept positional arguments: '{args[i]}'";
                    break;
            }

            if (options.ParseError != null)
                break;
        }

        return options;
    }

    private static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null, string? errorCode = null)
    {
        return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, message, exitCode, hint, errorCode: errorCode);
    }
}

internal sealed class ReportCommandOptions
{
    public string DbPath { get; set; } = string.Empty;
    public string? OutputPath { get; set; }
    public bool Json { get; set; }
    public bool ShowHelp { get; set; }
    public bool IncludeLog { get; set; } = true;
    public bool IncludeArgs { get; set; }
    public int LogLines { get; set; } = ReportCommandRunner.DefaultLogLines;
    public string? ParseError { get; set; }
}

internal sealed class ReportBundle
{
    public List<(string Name, byte[] Bytes)> Files { get; } = new();
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UnixEpoch;
    public List<ReportSchemaTable> SchemaTables { get; set; } = new();
    public bool SchemaTablesTruncated { get; set; }
    public bool LogIncluded { get; set; }
    public bool DbIncluded { get; set; }
    public string? DbPath { get; set; }
    public int LogLinesIncluded { get; set; }
    public bool LogTailTruncated { get; set; }
    public bool LogLineCharsTruncated { get; set; }

    public void AddText(string name, string content) =>
        Files.Add((name, Encoding.UTF8.GetBytes(content)));
}

internal sealed record ReportSchemaTable(string Name, long RowCount, bool RowCountTruncated = false);

internal sealed record ReportLogTailReadResult(
    IReadOnlyList<string> Lines,
    bool LinesTruncated,
    bool BytesTruncated,
    bool LineCharsTruncated);

internal sealed class ReportRedactionCounter
{
    private readonly Dictionary<string, int> categories = new(StringComparer.Ordinal);
    private int total;

    public void Observe(string rawLine, string redactedLine)
    {
        var placeholders = CountOccurrences(redactedLine, ReportCommandRunner.RedactedPlaceholder);
        if (placeholders == 0)
            return;

        total += placeholders;
        var categorized = 0;
        categorized += CountKeyRedaction(redactedLine, "args", "args");
        categorized += CountKeyRedaction(redactedLine, "cwd", "path_fields");
        categorized += CountKeyRedaction(redactedLine, "process_path", "path_fields");
        categorized += CountKeyRedaction(redactedLine, "base_dir", "path_fields");
        categorized += CountKeyRedaction(redactedLine, "db", "path_fields");
        categorized += CountKeyRedaction(redactedLine, "path", "path_fields");
        var uncategorized = placeholders - categorized;
        if (uncategorized > 0)
            Add("sensitive_values", uncategorized);

        if (!string.Equals(rawLine, redactedLine, StringComparison.Ordinal) && redactedLine.Contains("://", StringComparison.Ordinal))
            Add("url_or_query", 1);
    }

    public ReportRedactionSummary ToSummary()
        => new(total, categories.Count == 0 ? new Dictionary<string, int>(StringComparer.Ordinal) : new Dictionary<string, int>(categories, StringComparer.Ordinal));

    private int CountKeyRedaction(string line, string key, string category)
    {
        var count = CountOccurrences(line, key + "=" + ReportCommandRunner.RedactedPlaceholder);
        if (count > 0)
            Add(category, count);
        return count;
    }

    private void Add(string category, int count)
    {
        if (count <= 0)
            return;
        categories[category] = categories.TryGetValue(category, out var existing) ? existing + count : count;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while (offset < text.Length)
        {
            var index = text.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
                break;
            count++;
            offset = index + value.Length;
        }
        return count;
    }
}

internal sealed record ReportSupportManifest(
    int ManifestVersion,
    string GeneratedAtUtc,
    ReportManifestArtifact Artifact,
    ReportManifestLimits Limits,
    ReportManifestBundle Bundle,
    ReportRedactionSummary Redactions,
    ReportManifestOmissions Omissions,
    ReportReadinessSnapshot Readiness,
    ReportDiagnosticSummary Diagnostics);

internal sealed record ReportManifestArtifact(
    string Format,
    string MediaType,
    List<string> RecommendedExtensions,
    bool JsonMetadataStdoutOnly);

internal sealed record ReportManifestLimits(
    int MaxSchemaTables,
    int MaxSchemaTableNameDisplayChars,
    int MaxSchemaRowCountScanRows,
    int MaxLogLines,
    int MaxLogFileTailBytes,
    int MaxLogTailLineChars,
    int MaxRecentLogFiles);

internal sealed record ReportManifestBundle(
    bool DbIncluded,
    bool LogIncluded,
    bool IncludeArgs,
    int Files,
    int SchemaTables,
    int LogLinesIncluded);

internal sealed record ReportRedactionSummary(int Total, Dictionary<string, int> Categories)
{
    public static ReportRedactionSummary Empty { get; } = new(0, new Dictionary<string, int>(StringComparer.Ordinal));
}

internal sealed record ReportManifestOmissions(
    List<string> Schema,
    List<string> Log,
    List<string> Status);

internal sealed record ReportReadinessSnapshot(
    string Source,
    string? UnavailableReason,
    bool? GraphTableAvailable,
    bool? IssuesTableAvailable,
    bool? FileIssuesDataCurrent,
    bool? MigrationInProgress,
    [property: System.Text.Json.Serialization.JsonPropertyName("csharp_symbol_name_ready")]
    bool? CSharpSymbolNameReady,
    [property: System.Text.Json.Serialization.JsonPropertyName("csharp_metadata_target_ready")]
    bool? CSharpMetadataTargetReady,
    bool? SqlGraphContractReady,
    bool? HotspotFamilyReady,
    bool? FoldReady,
    List<string>? DegradedFields)
{
    public static ReportReadinessSnapshot Unavailable(string reason, List<string> degradedFields)
        => new(
            Source: "unavailable",
            UnavailableReason: reason,
            GraphTableAvailable: null,
            IssuesTableAvailable: null,
            FileIssuesDataCurrent: null,
            MigrationInProgress: null,
            CSharpSymbolNameReady: null,
            CSharpMetadataTargetReady: null,
            SqlGraphContractReady: null,
            HotspotFamilyReady: null,
            FoldReady: null,
            DegradedFields: degradedFields);
}

internal sealed record ReportDiagnosticSummary(
    ReportExtractorDiagnosticSummary Extractors,
    ReportHookDiagnosticSummary Hooks,
    ReportConfigDiagnosticSummary Config);

internal sealed record ReportExtractorDiagnosticSummary(
    int PluginAssemblyCount,
    int PatternConfigCount,
    int SymbolExtractorCount,
    int ReferenceExtractorCount,
    int SkippedFileCount,
    int DiagnosticCount,
    bool DiagnosticsTruncated);

internal sealed record ReportHookDiagnosticSummary(
    int HookCount,
    int DiagnosticCount,
    long CallbackBudgetMs);

internal sealed record ReportConfigDiagnosticSummary(
    bool RawValuesIncluded,
    List<string> Notes);

internal sealed record ReportMetadata(
    string Version,
    string GeneratedAtUtc,
    string DotNetRuntimeVersion,
    string FrameworkDescription,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    bool IsLittleEndian);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReportMetadata))]
internal partial class ReportMetadataJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReportSupportManifest))]
internal partial class ReportSupportManifestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
