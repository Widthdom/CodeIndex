using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static readonly string[] AcceptedBackfillFoldFlags =
    [
        "--db", "--json", "--dry-run", "--help",
        "--no-checkpoint", "--show-paths",
    ];
    private static readonly (string Name, string[] Columns)[] OptimizeObjectDefinitions =
    [
        ("files", ["path", "lang", "checksum"]),
        ("chunks", ["content"]),
        ("symbols", ["kind", "sub_kind", "name", "signature", "container_kind", "container_name", "container_qualified_name", "family_key", "visibility", "return_type", "metadata_target_source", "name_folded"]),
        ("reference_lines", ["context"]),
        ("symbol_references", ["symbol_name", "reference_kind", "context", "container_kind", "container_name", "symbol_name_folded", "container_name_folded"]),
        ("file_issues", ["kind", "message", "origin", "severity"]),
        ("codeindex_meta", ["key", "value"]),
        ("fts_chunks_data", ["block"]),
        ("fts_chunks_idx", ["term"]),
        ("fts_chunks_content", ["c0"]),
        ("fts_chunks_docsize", ["sz"]),
        ("fts_chunks_config", ["k", "v"]),
        ("fts_chunks_trigram_data", ["block"]),
        ("fts_chunks_trigram_idx", ["term"]),
        ("fts_chunks_trigram_content", ["c0"]),
        ("fts_chunks_trigram_docsize", ["sz"]),
        ("fts_chunks_trigram_config", ["k", "v"]),
    ];
    private static readonly string[] OptimizeFtsObjectNames =
    [
        .. OptimizeObjectDefinitions
            .Where(definition => definition.Name.StartsWith("fts_chunks_", StringComparison.Ordinal))
            .Select(definition => definition.Name),
    ];

    public static int RunBackfillFold(string[] cmdArgs, JsonSerializerOptions jsonOptions) =>
        RunBackfillFold(cmdArgs, jsonOptions, cancellationForTesting: null);

    public static int RunOptimizeFts(string[] cmdArgs, JsonSerializerOptions jsonOptions) =>
        RunOptimizeFts(cmdArgs, jsonOptions, forceLogicalObjectSizeFallbackForTesting: false);

    internal static int RunOptimizeFts(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        bool forceLogicalObjectSizeFallbackForTesting)
    {
        var options = ParseOptimizeFtsArgs(cmdArgs);
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
                "Run `cdidx optimize --help` to see the supported command shape.",
                CommandErrorCodes.UsageError);

        var dbUriValidationExitCode = ValidateDbPathFileUri(options.DbPath, "optimize", options.Json, jsonOptions);
        if (dbUriValidationExitCode != null)
            return dbUriValidationExitCode.Value;

        if (!options.DryRun && DbPathResolver.UriRequestsReadOnly(options.DbPath))
            return MaintenanceDatabaseErrorWriter.Write(
                options.Json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.Create(
                    "optimize",
                    options.DbPath,
                    options.ShowPaths,
                    MaintenanceDatabaseFailureKind.NotWritable));

        return RunOptimizeFtsForDb(
            Path.GetFullPath(DbPathResolver.NormalizeDbPath(options.DbPath)),
            options.Json,
            jsonOptions,
            projectPath: null,
            options.DryRun,
            forceLogicalObjectSizeFallbackForTesting,
            options.ShowPaths,
            diagnosticDbPath: options.DbPath);
    }

    private static int RunOptimizeFtsForDb(
        string dbPath,
        bool json,
        JsonSerializerOptions jsonOptions,
        string? projectPath,
        bool dryRun = false,
        bool forceLogicalObjectSizeFallbackForTesting = false,
        bool showPaths = false,
        string? diagnosticDbPath = null)
    {
        var errorDbPath = diagnosticDbPath ?? dbPath;
        if (dryRun)
            return RunOptimizeFtsPreviewForDb(
                dbPath,
                json,
                jsonOptions,
                forceLogicalObjectSizeFallbackForTesting,
                showPaths,
                errorDbPath);

        if (!DbContext.TryValidateExistingCodeIndexDb(
                dbPath,
                requireWritable: true,
                requireSupportedUserVersion: false,
                out _,
                out _,
                out _,
                out var validationFailure,
                out var validationException))
            return MaintenanceDatabaseErrorWriter.Write(
                json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.FromValidation(
                    "optimize",
                    errorDbPath,
                    showPaths,
                    validationFailure,
                    validationException));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var lockPath = IndexLock.GetLockPath(dbPath);
            using var indexLock = IndexLock.Acquire(lockPath, projectPath ?? Path.GetDirectoryName(dbPath) ?? Environment.CurrentDirectory);
            using var db = new DbContext(DbOpenIntent.Repair, dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db);
            var before = writer.GetFtsIncrementalWritesSinceOptimize();
            writer.OptimizeFts();
            stopwatch.Stop();
            var after = writer.GetFtsIncrementalWritesSinceOptimize();

            if (json)
            {
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                CommandOutputWriter.WriteLine(JsonSerializer.Serialize(
                    new OptimizeFtsJsonResult("success", dbPath, before, after, stopwatch.ElapsedMilliseconds),
                    jsonContext.OptimizeFtsJsonResult));
            }
            else
            {
                CommandOutputWriter.WriteLine("Optimized FTS5 index.");
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("DB", dbPath, indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Writes before", before.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Writes after", after.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Elapsed", ConsoleUi.FormatDuration(stopwatch.Elapsed), indent: "  "));
            }

            return CommandExitCodes.Success;
        }
        catch (IndexLockConflictException ex)
        {
            var holderDescription = DescribeLockHolder(ex.Holder);
            return MaintenanceDatabaseErrorWriter.Write(
                json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.Create(
                    "optimize",
                    errorDbPath,
                    showPaths,
                    MaintenanceDatabaseFailureKind.Locked,
                    details: string.IsNullOrEmpty(holderDescription)
                        ? null
                        : [holderDescription]));
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return MaintenanceDatabaseErrorWriter.Write(
                json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.FromException(
                    "optimize",
                    errorDbPath,
                    showPaths,
                    ex));
        }
    }

    private static int RunOptimizeFtsPreviewForDb(
        string dbPath,
        bool json,
        JsonSerializerOptions jsonOptions,
        bool forceLogicalObjectSizeFallbackForTesting,
        bool showPaths,
        string errorDbPath)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!File.Exists(LongPath.EnsureWindowsPrefix(dbPath)))
        {
            return MaintenanceDatabaseErrorWriter.Write(
                json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.Create(
                    "optimize",
                    errorDbPath,
                    showPaths,
                    MaintenanceDatabaseFailureKind.Missing));
        }

        try
        {
            var (lockState, lockHolder) = IndexLock.ProbeReadOnly(IndexLock.GetLockPath(dbPath));
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            if (!db.TryValidateIsCodeIndexDb(out _))
            {
                return MaintenanceDatabaseErrorWriter.Write(
                    json,
                    jsonOptions,
                    MaintenanceDatabaseErrorClassifier.Create(
                        "optimize",
                        errorDbPath,
                        showPaths,
                        MaintenanceDatabaseFailureKind.NotDatabase));
            }

            var status = new DbReader(db).GetStatus(includeDatabaseSizeAttribution: false);
            var objectSizes = ReadOptimizeObjectSizes(
                db,
                forceLogicalObjectSizeFallbackForTesting,
                out var objectSizesMeasurement,
                out var objectSizesUnavailableReason);
            var writesSinceOptimize = ParseNonNegativeLong(
                db.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
            var estimatedDurationMs = ParseNullableNonNegativeLong(
                db.GetMetaString(DbWriter.FtsLastOptimizeDurationMsMetaKey));
            var coreTableSizeBytes = SumObjectSizes(
                objectSizes,
                "files",
                "chunks",
                "symbols",
                "reference_lines",
                "symbol_references",
                "file_issues",
                "codeindex_meta");
            var ftsSizeBytes = SumObjectSizes(
                objectSizes,
                OptimizeFtsObjectNames);
            var optimizationRecommended = writesSinceOptimize >= DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold;
            stopwatch.Stop();

            var result = new OptimizeFtsPreviewJsonResult
            {
                Status = "dry_run",
                DryRun = true,
                DbPath = dbPath,
                WritesSinceOptimizeBefore = checked((int)Math.Min(writesSinceOptimize, int.MaxValue)),
                WritesSinceOptimizeAfter = checked((int)Math.Min(writesSinceOptimize, int.MaxValue)),
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                EstimatedDurationMs = estimatedDurationMs,
                DbSizeBytes = TryGetFileSize(dbPath),
                WalSizeBytes = TryGetFileSize(dbPath + "-wal") ?? 0,
                PageCount = status.DbPragmaSettings?.PageCount,
                FreelistCount = status.DbPragmaSettings?.FreelistCount,
                PageSize = status.DbPragmaSettings?.PageSize,
                FreelistRatio = status.MaintenanceGuidance?.FreelistRatio,
                EstimatedBytesReclaimable = status.MaintenanceGuidance?.EstimatedBytesReclaimable,
                CoreTableSizeBytes = objectSizes.Count > 0 ? coreTableSizeBytes : null,
                FtsSizeBytes = objectSizes.Count > 0 ? ftsSizeBytes : null,
                ObjectSizeBytes = objectSizes.Count > 0 ? objectSizes : null,
                ObjectSizesAvailable = objectSizes.Count > 0,
                ObjectSizesMeasurement = objectSizesMeasurement,
                ObjectSizesUnavailableReason = objectSizesUnavailableReason,
                LockState = lockState,
                LockHolderVerification = lockHolder?.Verification.ToString().ToLowerInvariant(),
                WouldAcquireExclusiveIndexLock = true,
                OptimizationRecommended = optimizationRecommended,
                RecommendationReason = optimizationRecommended
                    ? "incremental_write_threshold_reached"
                    : "incremental_write_threshold_not_reached",
                Readiness = new OptimizeFtsReadinessJsonResult
                {
                    FoldReady = status.FoldReady,
                    GraphTableAvailable = status.GraphTableAvailable,
                    IssuesTableAvailable = status.IssuesTableAvailable,
                    FileIssuesDataCurrent = status.FileIssuesDataCurrent,
                    MigrationInProgress = status.MigrationInProgress,
                    SqlGraphContractReady = status.SqlGraphContractReady,
                    HotspotFamilyReady = status.HotspotFamilyReady,
                    CsharpSymbolNameReady = status.CSharpSymbolNameReady,
                    CsharpMetadataTargetReady = status.CSharpMetadataTargetReady,
                    IndexNewerThanReader = status.IndexNewerThanReader,
                },
                PlannedOperations =
                [
                    "acquire_exclusive_index_lock",
                    "initialize_or_migrate_schema",
                    "merge_fts5_segments",
                    "reset_incremental_write_counter",
                    "stamp_last_optimized_at",
                    "stamp_last_optimize_duration",
                ],
                SourceDatabaseUnchanged = true,
            };

            if (json)
            {
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                CommandOutputWriter.WriteLine(JsonSerializer.Serialize(result, jsonContext.OptimizeFtsPreviewJsonResult));
            }
            else
            {
                CommandOutputWriter.WriteLine("FTS5 optimize preview (read-only; no changes made).");
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("DB", dbPath, indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("DB size", ConsoleUi.FormatBytes(result.DbSizeBytes ?? 0), indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Core size", ConsoleUi.FormatBytes(result.CoreTableSizeBytes ?? 0), indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("FTS size", ConsoleUi.FormatBytes(result.FtsSizeBytes ?? 0), indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Free pages", (result.FreelistCount ?? 0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture), indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("FTS writes", result.WritesSinceOptimizeBefore.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Index lock", result.LockState, indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine(
                    "Readiness",
                    result.Readiness == null
                        ? "unavailable"
                        : $"fold={(result.Readiness.FoldReady ? "ready" : "not-ready")}, graph={(result.Readiness.GraphTableAvailable ? "ready" : "not-ready")}, issues={(result.Readiness.IssuesTableAvailable ? "ready" : "not-ready")}, migration={(result.Readiness.MigrationInProgress ? "in-progress" : "idle")}",
                    indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine(
                    "Est. duration",
                    result.EstimatedDurationMs is { } durationEstimateMs
                        ? ConsoleUi.FormatDuration(TimeSpan.FromMilliseconds(durationEstimateMs))
                        : "unavailable",
                    indent: "  "));
                CommandOutputWriter.WriteLine(ConsoleUi.FormatSummaryLine("Recommended", result.OptimizationRecommended ? "yes" : "not yet", indent: "  "));
                CommandOutputWriter.WriteLine("  Planned operations:");
                foreach (var operation in result.PlannedOperations ?? [])
                    CommandOutputWriter.WriteLine($"    - {operation}");
            }

            return CommandExitCodes.Success;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return MaintenanceDatabaseErrorWriter.Write(
                json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.FromException(
                    "optimize",
                    errorDbPath,
                    showPaths,
                    ex));
        }
    }

    private static Dictionary<string, long> ReadOptimizeObjectSizes(
        DbContext db,
        bool forceLogicalObjectSizeFallbackForTesting,
        out string? measurement,
        out string? unavailableReason)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!forceLogicalObjectSizeFallbackForTesting)
        {
            try
            {
                using var cmd = db.Connection.CreateCommand();
                var objectNameParameters = OptimizeObjectDefinitions
                    .Select((_, index) => $"@objectName{index}")
                    .ToArray();
                cmd.CommandText = $"""
                    SELECT name, SUM(pgsize)
                    FROM dbstat
                    WHERE name IN ({string.Join(", ", objectNameParameters)})
                    GROUP BY name
                    ORDER BY name
                    """;
                for (var index = 0; index < OptimizeObjectDefinitions.Length; index++)
                {
                    SqliteCommandPolicy.AddText(
                        cmd,
                        objectNameParameters[index],
                        OptimizeObjectDefinitions[index].Name);
                }
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    sizes[reader.GetString(0)] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                measurement = "dbstat_page_bytes";
                unavailableReason = null;
                return sizes;
            }
            catch (SqliteException)
            {
                // Fall through to the portable logical-payload measurement.
                // portable な logical-payload 計測へフォールバックする。
            }
        }

        sizes = ReadOptimizeLogicalPayloadSizes(db);
        measurement = sizes.Count > 0 ? "logical_payload_bytes" : null;
        unavailableReason = sizes.Count > 0 ? null : "size_measurement_unavailable";

        return sizes;
    }

    private static Dictionary<string, long> ReadOptimizeLogicalPayloadSizes(DbContext db)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, candidateColumns) in OptimizeObjectDefinitions)
        {
            try
            {
                var presentColumns = ReadOptimizeObjectColumns(db, name);
                var measuredColumns = candidateColumns.Where(presentColumns.Contains).ToArray();
                if (measuredColumns.Length == 0)
                    continue;

                var byteExpressions = measuredColumns.Select(column =>
                    $"length(CAST(COALESCE({QuoteOptimizeIdentifier(column)}, X'') AS BLOB))");
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText =
                    $"SELECT COALESCE(SUM({string.Join(" + ", byteExpressions)}), 0) FROM {QuoteOptimizeIdentifier(name)}";
                sizes[name] = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (SqliteException)
            {
                // Older external-content FTS layouts can omit individual shadow/core tables.
                // 古い external-content FTS layout では一部 shadow/core table が無い場合がある。
            }
        }

        return sizes;
    }

    private static HashSet<string> ReadOptimizeObjectColumns(DbContext db, string objectName)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info(@object_name)";
        cmd.Parameters.AddWithValue("@object_name", objectName);
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static string QuoteOptimizeIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static long SumObjectSizes(
        IReadOnlyDictionary<string, long> objectSizes,
        params string[] names)
    {
        long total = 0;
        foreach (var name in names)
        {
            if (objectSizes.TryGetValue(name, out var size))
                total = checked(total + size);
        }
        return total;
    }

    private static long ParseNonNegativeLong(string? value)
        => long.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            && parsed > 0
                ? parsed
                : 0;

    private static long? ParseNullableNonNegativeLong(string? value)
        => long.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            && parsed >= 0
                ? parsed
                : null;

    private static long? TryGetFileSize(string path)
    {
        try
        {
            var info = new FileInfo(LongPath.EnsureWindowsPrefix(path));
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    internal static int RunBackfillFold(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        CancellationTokenSource? cancellationForTesting)
    {
        var options = ParseBackfillFoldArgs(cmdArgs);
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        using var ownedCancellation = cancellationForTesting == null ? new CancellationTokenSource() : null;
        var backfillCancellation = cancellationForTesting ?? ownedCancellation!;
        using var cancelKeyPressRegistration = cancellationForTesting == null
            ? RegisterIndexCancelKeyPress(backfillCancellation)
            : NullDisposable.Instance;
        using var terminateSignalRegistration = cancellationForTesting == null
            ? RegisterIndexTerminateSignal(backfillCancellation)
            : NullDisposable.Instance;

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
                "Run `cdidx backfill-fold --help` to see the supported command shape.",
                CommandErrorCodes.UsageError);

        if (!options.DryRun && DbPathResolver.UriRequestsReadOnly(options.DbPath))
        {
            return MaintenanceDatabaseErrorWriter.Write(
                options.Json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.Create(
                    "backfill-fold",
                    options.DbPath,
                    options.ShowPaths,
                    MaintenanceDatabaseFailureKind.NotWritable));
        }

        if (!DbContext.TryValidateExistingCodeIndexDb(
                options.DbPath,
                requireWritable: !options.DryRun,
                requireSupportedUserVersion: false,
                out _,
                out _,
                out _,
                out var validationFailure,
                out var validationException))
            return MaintenanceDatabaseErrorWriter.Write(
                options.Json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.FromValidation(
                    "backfill-fold",
                    options.DbPath,
                    options.ShowPaths,
                    validationFailure,
                    validationException));

        try
        {
            using var db = new DbContext(
                options.DryRun ? DbOpenIntent.QueryOnly : DbOpenIntent.Migration,
                options.DbPath);
            if (!options.DryRun)
                db.InitializeSchema();
            var writer = new DbWriter(db);
            if (writer.TryGetNewerCSharpSymbolNameContractVersion(out var newerCSharpContract))
            {
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    $"C# symbol-name contract version {newerCSharpContract} is newer than supported version {DbContext.CSharpSymbolNameContractVersion}",
                    CommandExitCodes.DatabaseError,
                    "Use the same or a newer CodeIndex version that wrote this database; this version will not rewrite or downgrade its C# identities.",
                    CommandErrorCodes.DbError);
            }

            var userVersionBefore = db.GetUserVersion();
            var foldReadyBefore = (userVersionBefore & DbContext.FoldReadyFlag) != 0;
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var storedFoldVersion = db.GetMetaString("fold_key_version");
            var storedFoldFingerprint = db.GetMetaString("fold_key_fingerprint");
            var foldMetadataCurrentBefore = storedFoldVersion == currentFoldVersion
                && storedFoldFingerprint == currentFoldFingerprint;
            var csharpSymbolNameContractUpgradeRequired =
                writer.HasAnyFilesWithLanguage("csharp")
                && !string.Equals(
                db.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey),
                DbContext.CSharpSymbolNameContractVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
            if (csharpSymbolNameContractUpgradeRequired
                && !writer.CanReconstructCSharpExplicitInterfaceIdentitiesFromPersistedRows())
            {
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    "C# explicit-interface identities cannot be reconstructed because legacy symbol signatures are missing",
                    CommandExitCodes.DatabaseError,
                    "Refresh the C# files with `cdidx index <projectPath>` (or rebuild the index) before retrying `cdidx backfill-fold`.",
                    CommandErrorCodes.DbError);
            }
            foldReadyBefore = foldReadyBefore && foldMetadataCurrentBefore;
            // Missing or mismatched fold metadata means persisted keys may have been generated
            // by a different fold algorithm/runtime, so refresh every row from source names.
            // fold metadata 未記録 / 不一致時は全行再計算して version/runtime skew を解消する。
            var rewriteAll = writer.ResolveFoldBackfillRewriteAll(!foldMetadataCurrentBefore);

            var symbols = 0;
            var symbolReferences = 0;
            var verified = false;
            var userVersionAfter = userVersionBefore;

            if (options.DryRun)
            {
                (symbols, symbolReferences) = writer.CountBackfillFoldedColumns(rewriteAll);
            }
            else
            {
                if (!options.NoCheckpoint)
                    DbCommandRunner.CreateAutomaticCheckpoint(options.DbPath);

                (symbols, symbolReferences) = writer.BackfillFoldedColumns(
                    rewriteAll,
                    backfillCancellation.Token);
                // Row rewrites commit before the final FoldReady stamp so interrupted
                // backfills can resume from the remaining rows.
                // 行更新は FoldReady stamp より前に永続化し、中断後に残り行から再開できるようにする。
                using var transaction = writer.BeginTransaction(backfillCancellation.Token, "backfill fold readiness stamp");
                verified = writer.MarkFoldReady();
                if (!verified)
                {
                    return WriteCommandError(
                        options.Json,
                        jsonOptions,
                        "folded-name backfill verification failed: some rows still have NULL folded values",
                        CommandExitCodes.DatabaseError,
                        "Retry `cdidx backfill-fold`. If the DB still does not verify, rebuild it with `cdidx index <projectPath> --rebuild`.",
                        CommandErrorCodes.DbError);
                }
                writer.MarkCSharpSymbolNameContractReady();

                transaction.Commit();
                userVersionAfter = db.GetUserVersion();
            }
            var foldMetadataCurrentAfter = options.DryRun
                ? foldMetadataCurrentBefore
                : true;
            var foldReadyAfter = (userVersionAfter & DbContext.FoldReadyFlag) != 0
                && foldMetadataCurrentAfter;
            var wasAlreadyComplete = foldReadyBefore && !rewriteAll && symbols == 0 && symbolReferences == 0;

            if (options.Json)
            {
                CommandOutputWriter.WriteLine(JsonSerializer.Serialize(new BackfillFoldJsonResult(
                    symbols,
                    symbolReferences,
                    rewriteAll,
                    options.DryRun,
                    wasAlreadyComplete,
                    foldReadyBefore,
                    foldReadyAfter,
                    verified,
                    userVersionBefore,
                    userVersionAfter,
                    foldReadyAfter), jsonContext.BackfillFoldJsonResult));
            }
            else
            {
                CommandOutputWriter.WriteLine(options.DryRun
                    ? "Previewing folded-name column backfill ..."
                    : "Backfilling folded-name columns ...");
                var verb = options.DryRun ? "would be rewritten" : "rewritten";
                CommandOutputWriter.WriteLine($"  symbols:            {ConsoleUi.Counted(symbols, "row", format: "N0")} {verb}");
                CommandOutputWriter.WriteLine($"  symbol_references:  {ConsoleUi.Counted(symbolReferences, "row", format: "N0")} {verb}");
                if (rewriteAll)
                    CommandOutputWriter.WriteLine("  mode:               full folded-key refresh (fold metadata missing or mismatched)");
                CommandOutputWriter.WriteLine($"  already complete:   {(wasAlreadyComplete ? "yes" : "no")}");
                CommandOutputWriter.WriteLine($"  fold_ready:         {foldReadyBefore} -> {foldReadyAfter}");
                if (!options.DryRun)
                {
                    CommandOutputWriter.WriteLine($"  verified:           {(verified ? "yes" : "no")}");
                    CommandOutputWriter.WriteLine($"  stamp:              FoldReady bit set (user_version: {userVersionBefore} -> {userVersionAfter})");
                }
            }

            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "folded-name backfill cancelled before it could complete.",
                CommandExitCodes.CancelledBySignal,
                "Rerun `cdidx backfill-fold` when you are ready to resume; the cancelled transaction was rolled back.",
                CommandErrorCodes.Interrupted);
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return MaintenanceDatabaseErrorWriter.Write(
                options.Json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.FromException(
                    "backfill-fold",
                    options.DbPath,
                    options.ShowPaths,
                    ex));
        }
    }

    private static OptimizeFtsCommandOptions ParseOptimizeFtsArgs(string[] args)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        bool json = false;
        bool dryRun = false;
        bool showPaths = false;
        string? parseError = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--show-paths":
                    showPaths = true;
                    break;
                case "--help" or "-h":
                    return new OptimizeFtsCommandOptions { ShowHelp = true };
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                        parseError ??= $"unknown option '{args[i]}'";
                    else
                        dbPath = args[i];
                    break;
            }
        }

        return new OptimizeFtsCommandOptions
        {
            DbPath = dbPath,
            Json = json,
            DryRun = dryRun,
            ShowPaths = showPaths,
            ParseError = parseError,
        };
    }

    private static BackfillFoldCommandOptions ParseBackfillFoldArgs(string[] args)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        var json = false;
        var dryRun = false;
        var noCheckpoint = false;
        var showPaths = false;
        string? parseError = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-checkpoint":
                    noCheckpoint = true;
                    break;
                case "--show-paths":
                    showPaths = true;
                    break;
                case "--help" or "-h":
                    return new BackfillFoldCommandOptions { ShowHelp = true, DbPath = dbPath, Json = json, DryRun = dryRun, NoCheckpoint = noCheckpoint, ShowPaths = showPaths };
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        parseError ??= BuildUnknownBackfillFoldOptionError(args[i]);
                    }
                    else
                    {
                        parseError ??= $"backfill-fold does not accept positional arguments: '{args[i]}'";
                    }
                    break;
            }
        }

        return new BackfillFoldCommandOptions
        {
            DbPath = dbPath,
            Json = json,
            DryRun = dryRun,
            NoCheckpoint = noCheckpoint,
            ShowPaths = showPaths,
            ParseError = parseError,
        };
    }

    private static string BuildUnknownBackfillFoldOptionError(string token)
    {
        var name = TrimInlineValue(token);
        var suggestion = ConsoleUi.FindClosestMatch(name, AcceptedBackfillFoldFlags);
        var displayToken = ConsoleUi.FormatBoundedValue(token);
        return suggestion == null
            ? $"unknown option '{displayToken}'"
            : $"unknown option '{displayToken}'\nDid you mean: {suggestion}?";
    }
}
