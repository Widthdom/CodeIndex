using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int WithDb(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        Func<DbReader, int> action,
        Action<int>? afterProfile = null,
        CancellationToken cancellationToken = default)
    {
        var dbPath = options.DbPath;
        var batchReader = s_batchReader != null && !options.DbPathExplicit
            ? s_batchReader
            : null;
        if (batchReader == null)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                CommandErrorWriter.WriteStderr(BuildMissingOptionValueError("--db"));
                return CommandExitCodes.UsageError;
            }

            // Allow SQLite URI forms (file:///abs/path?immutable=1 etc.) so users and AI agents
            // on read-only mounts / sandboxes can opt into the immutable read-only escape hatch
            // explicitly when the automatic DbContext fallback cannot recover. File.Exists is
            // skipped for URI-shaped inputs because they may carry query params and schemes that
            // are meaningless to the filesystem API but are understood by SQLite.
            // URI 形式の --db を受け入れるため、file: で始まる値は File.Exists チェックをスキップ。
            var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            var fileExistsPath = dbPath;
            if (isUri)
            {
                if (!DbPathResolver.TryNormalizeDbPath(dbPath, out fileExistsPath, out var parseError))
                {
                    var boundedDbPath = FormatDbDiagnosticValue(dbPath);
                    CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: invalid --db file URI: {SqliteFileUri.FormatParseError(parseError)}");
                    CommandErrorWriter.WriteStderr($"Hint: pass a valid SQLite file URI such as `file:///absolute/path/to/codeindex.db?immutable=1`; the --db value resolved to: {boundedDbPath}");
                    GlobalToolLog.Error($"invalid_db_file_uri db={FormatLogValue(dbPath)} exception={FormatLogValue(parseError?.ToString() ?? "<unknown>")}");
                    return CommandExitCodes.DatabaseError;
                }
            }

            if (!fileExistsPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(LongPath.EnsureWindowsPrefix(fileExistsPath)))
            {
                var resolvedPath = Path.GetFullPath(fileExistsPath);
                var displayPath = FormatDbDiagnosticValue(resolvedPath);
                CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbNotFound}]: database not found at {displayPath}");
                if (isUri)
                    CommandErrorWriter.WriteStderr($"Hint: the --db path resolved to: {displayPath}");
                CommandErrorWriter.WriteStderr("Hint: create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.");
                return CommandExitCodes.DatabaseError;
            }
        }

        Database.DbDebug.ResetContext();
        var profiling = options.Profile || options.Verbose || options.SlowQueryMs.HasValue;
        if (profiling)
            Database.DbDebug.BeginProfile(options.SlowQueryMs);
        DbContext? db = null;
        var databaseReadyForQueries = batchReader != null;
        try
        {
            DbReader reader;
            if (batchReader != null)
            {
                reader = batchReader;
            }
            else
            {
                db = new DbContext(dbPath, cancellationToken);
                if (!db.TryValidateIsCodeIndexDb(out var validationReason))
                    return WriteInvalidCodeIndexDbError(dbPath, validationReason, options.Json, jsonOptions);
                db.TryMigrateForRead();
                reader = new DbReader(db);
                databaseReadyForQueries = true;
            }

            reader.IncludeGenerated = options.IncludeGenerated;
            var previousProjectRoot = s_activeQueryProjectRoot;
            var projectRootResolution = s_batchReader != null && options.DbPathExplicit
                ? ResolveProjectRootForDbPath(dbPath, options.DbPathExplicit)
                : ResolveProjectFilterRoot(dbPath, options.DbPathExplicit);
            s_activeQueryProjectRoot = projectRootResolution.Root;
            int exitCode;
            var previousDiagnosticsReader = ActiveSqliteDiagnosticsReader.Value;
            ActiveSqliteDiagnosticsReader.Value = reader;
            try
            {
                exitCode = reader.RunWithGeneratedScope(() => action(reader));
            }
            finally
            {
                ActiveSqliteDiagnosticsReader.Value = previousDiagnosticsReader;
                s_activeQueryProjectRoot = previousProjectRoot;
            }
            var profileEntries = profiling ? Database.DbDebug.EndProfile() : [];
            if (options.Profile)
                WriteProfilePayload(profileEntries, jsonOptions);
            if (options.Verbose)
                WriteVerboseQueryDebug(options, profileEntries, jsonOptions);
            afterProfile?.Invoke(exitCode);
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FtsQuerySyntaxException ex)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.FtsQuerySyntax}]: FTS5 query syntax: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            if (ex.Kind == FtsQuerySyntaxErrorKind.ColumnQualifier)
            {
                CommandErrorWriter.WriteStderr("Hint: `--fts` passes raw FTS5 syntax, so `:` is treated as a column qualifier. Drop `--fts` if you want literal-safe search.");
            }
            else
            {
                CommandErrorWriter.WriteStderr("Hint: `--fts` passes raw FTS5 syntax. Fix the query or drop `--fts` to use literal-safe search.");
            }
            return CommandExitCodes.UsageError;
        }
        catch (SearchGuardCandidateLimitException ex)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: guarded search is too broad: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            if (ex.CandidatePreviewPaths.Count > 0)
                CommandErrorWriter.WriteStderr($"Candidate files sampled before refusal: {string.Join(", ", ex.CandidatePreviewPaths)}.");
            if (ex.CandidatePreviewLanguages.Count > 0)
                CommandErrorWriter.WriteStderr($"Candidate languages sampled before refusal: {string.Join(", ", ex.CandidatePreviewLanguages)}.");
            CommandErrorWriter.WriteStderr("Hint: narrow the search with more specific query text, --lang, --path, or --exclude-tests, or reduce pagination offset before retrying guarded search.");
            CommandErrorWriter.WriteStderr("Hint: use `--count`, `--count-by path`, or `--format count` without guard filters to size the broad query before adding require/reject guards.");
            return CommandExitCodes.UsageError;
        }
        catch (SearchQueryLimitException ex)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            CommandErrorWriter.WriteStderr("Hint: shorten the search text or split generated input into smaller literal queries.");
            return CommandExitCodes.UsageError;
        }
        catch (BatchOutputCaptureLimitExceededException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            if (ex is CodeIndexException codeIndexException)
            {
                CodeIndexExceptionFormatter.Write(
                    codeIndexException,
                    options.Json ? ["--json"] : [],
                    jsonOptions);
                Database.DbDebug.DumpToStderr(ex);
                return ProgramRunner.MapCodeIndexExceptionExitCode(codeIndexException.Code);
            }

            if (ex is SqliteException sqliteEx)
            {
                if (sqliteEx.SqliteErrorCode == 13)
                {
                    CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.TempStoreExhausted}]: SQLite temp-store exhausted while evaluating this query.");
                    CommandErrorWriter.WriteStderr("Hint: narrow the query with `--lang`, `--path`, or `--kind`, then retry with a freshly updated cdidx build if the problem persists.");
                    Database.DbDebug.DumpToStderr(ex);
                    return CommandExitCodes.DatabaseError;
                }

                // SQLITE_BUSY (5) and SQLITE_LOCKED (6) both mean a concurrent writer is
                // holding the database; surface E002_DB_LOCKED so scripts can implement
                // retry-with-backoff without substring-matching the prose message.
                // SQLITE_BUSY/LOCKED は別 writer によるロック競合なので、リトライ判断用に
                // E002_DB_LOCKED で機械可読に区別する。
                if (sqliteEx.SqliteErrorCode == 5 || sqliteEx.SqliteErrorCode == 6)
                {
                    CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbLocked}]: SQLite reported the database is locked or busy: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
                    CommandErrorWriter.WriteStderr("Hint: another process may be holding the database. Wait for it to finish, or retry with backoff.");
                    Database.DbDebug.DumpToStderr(ex);
                    return CommandExitCodes.DatabaseError;
                }
            }

            var jsonDatabaseOpenFailure = options.Json && !databaseReadyForQueries;
            var databaseExitCode = WriteDatabaseOpenFailureJsonAware(ex, dbPath, jsonDatabaseOpenFailure, jsonOptions);
            Database.DbDebug.DumpToStderr(ex);
            return databaseExitCode;
        }
        finally
        {
            db?.Dispose();
            if (profiling)
                Database.DbDebug.EndProfile();
            Database.DbDebug.ResetContext();
        }
    }

    private static int WriteInvalidCodeIndexDbError(
        string dbPath,
        string? validationReason,
        bool json,
        JsonSerializerOptions jsonOptions)
    {
        return WriteDatabaseCommandError(
            json,
            jsonOptions,
            $"{FormatDbDiagnosticValue(dbPath)} does not appear to be a valid CodeIndex database ({validationReason}).",
            "rebuild with `cdidx index <projectPath> --db <path>` to create a fresh database.",
            "database");
    }

    private static ProjectFilterRootResolution ResolveProjectRootForDbPath(string dbPath, bool dbPathExplicit)
    {
        var projectRoot = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit);
        if (!string.IsNullOrWhiteSpace(projectRoot))
            return new ProjectFilterRootResolution(Path.GetFullPath(projectRoot), null);

        return new ProjectFilterRootResolution(
            Path.GetFullPath(Environment.CurrentDirectory),
            ProjectFilterRootFallbackReasonCurrentDirectory);
    }

    private static string? GetDataDirectoryPath(string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) ||
            dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetDirectoryName(Path.GetFullPath(dbPath));
    }

    private static int WriteDatabaseOpenFailureJsonAware(
        Exception ex,
        string dbPath,
        bool json,
        JsonSerializerOptions jsonOptions)
    {
        GlobalToolLog.Error($"database_open_failed db={FormatLogValue(dbPath)} exception={FormatLogValue(ex.ToString())}");

        var unauthorized = FindException<UnauthorizedAccessException>(ex);
        if (unauthorized != null)
        {
            return WriteDatabaseCommandError(
                json,
                jsonOptions,
                $"database access denied: {CommandErrorWriter.FormatSanitizedExceptionMessage(unauthorized)}",
                MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()),
                DiagnosticRedactor.ClassifyException(unauthorized));
        }

        var io = FindException<IOException>(ex);
        if (io != null)
        {
            return WriteDatabaseCommandError(
                json,
                jsonOptions,
                $"database I/O error: {CommandErrorWriter.FormatSanitizedExceptionMessage(io)}",
                MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()),
                DiagnosticRedactor.ClassifyException(io));
        }

        var sqlite = FindException<SqliteException>(ex);
        if (sqlite != null)
        {
            if (sqlite.SqliteErrorCode == 14)
            {
                return WriteDatabaseCommandError(
                    json,
                    jsonOptions,
                    $"database access/open denied: {CommandErrorWriter.FormatSanitizedExceptionMessage(sqlite)}",
                    MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()),
                    DiagnosticRedactor.ClassifyException(sqlite));
            }

            if (sqlite.SqliteErrorCode == 11)
            {
                return WriteDatabaseCommandError(
                    json,
                    jsonOptions,
                    $"SQLite reported database corruption: {CommandErrorWriter.FormatSanitizedExceptionMessage(sqlite)}",
                    "rebuild the index with `cdidx index <projectPath> --rebuild`, or delete the broken `.cdidx/codeindex.db*` files and run `cdidx index <projectPath>` again.",
                    DiagnosticRedactor.ClassifyException(sqlite));
            }

            return WriteDatabaseCommandError(
                json,
                jsonOptions,
                $"SQLite database error ({sqlite.SqliteErrorCode}): {CommandErrorWriter.FormatSanitizedExceptionMessage(sqlite)}",
                MacProfileDetector.IsPermissionStyleSqliteError(sqlite)
                    ? MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent())
                    : "check `--db`, verify the index was written by a compatible cdidx version, or rebuild it with `cdidx index <projectPath> --rebuild`.",
                DiagnosticRedactor.ClassifyException(sqlite));
        }

        return WriteDatabaseCommandError(
            json,
            jsonOptions,
            $"database error: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
            "check `--db`, or rebuild the index with `cdidx index <projectPath>` if the DB may be stale or corrupted.",
            DiagnosticRedactor.ClassifyException(ex));
    }

    private static int WriteDatabaseCommandError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string message,
        string hint,
        string? category)
        => CommandErrorWriter.WriteJsonOrHuman(
            json,
            jsonOptions,
            message,
            CommandExitCodes.DatabaseError,
            TrimHintPrefix(hint),
            errorCode: CommandErrorCodes.DbError,
            category: category);

    private static string TrimHintPrefix(string hint)
    {
        const string prefix = "Hint:";
        return hint.StartsWith(prefix, StringComparison.Ordinal)
            ? hint[prefix.Length..].TrimStart()
            : hint;
    }

    private static T? FindException<T>(Exception ex)
        where T : Exception
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is T typed)
                return typed;
        }

        return null;
    }

    private static string FormatLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        return SqliteFileUri.TruncateDiagnosticValue(value)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }

    private static string FormatDbDiagnosticValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        return SqliteFileUri.TruncateDiagnosticValue(value)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }

    private static void WriteProfilePayload(IReadOnlyList<QueryProfileEntry> entries, JsonSerializerOptions jsonOptions)
    {
        var phases = new JsonArray();
        var queryPlan = new JsonArray();
        var queries = new JsonArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var name = "sql_" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var entry = entries[i];
            phases.Add(new JsonObject
            {
                ["name"] = name,
                ["elapsed_ms"] = Math.Round(entry.ElapsedMs, 3),
                ["rows_scanned"] = entry.RowsScanned,
            });
            var sql = Database.DbDebug.FormatSqlForProfile(
                entry.Sql,
                out var sqlTruncated,
                out var sqlRedactedChars);
            queries.Add(new JsonObject
            {
                ["name"] = name,
                ["sql"] = sql,
                ["sql_truncated"] = sqlTruncated,
                ["sql_redacted_chars"] = sqlRedactedChars,
            });
            foreach (var row in entry.QueryPlan)
            {
                queryPlan.Add(new JsonObject
                {
                    ["phase"] = name,
                    ["id"] = row.Id,
                    ["parent"] = row.Parent,
                    ["not_used"] = row.NotUsed,
                    ["detail"] = row.Detail,
                });
            }
        }

        Console.WriteLine(new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["phases"] = phases,
                ["query_plan"] = queryPlan,
                ["queries"] = queries,
                ["sql_text_limit_chars"] = Database.DbDebug.MaxSlowQuerySqlChars,
                ["redaction"] = $"SQL literals and path-like values are redacted; SQL text is capped at {Database.DbDebug.MaxSlowQuerySqlChars.ToString(CultureInfo.InvariantCulture)} characters per statement.",
            },
        }.ToJsonString(jsonOptions));
    }

    private static void WriteVerboseQueryDebug(QueryCommandOptions options, IReadOnlyList<QueryProfileEntry> entries, JsonSerializerOptions jsonOptions)
    {
        var elapsedMs = Math.Round(entries.Sum(entry => entry.ElapsedMs), 3);
        var rowsScanned = entries.Sum(entry => entry.RowsScanned);
        if (!options.Json)
        {
            CommandErrorWriter.WriteStderr($"DEBUG query: sql_statements={entries.Count} elapsed_ms={elapsedMs.ToString(CultureInfo.InvariantCulture)} rows_scanned={rowsScanned}");
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                CommandErrorWriter.WriteStderr(
                    $"DEBUG query sql_{i + 1}: elapsed_ms={Math.Round(entry.ElapsedMs, 3).ToString(CultureInfo.InvariantCulture)} rows_scanned={entry.RowsScanned}");
            }
            return;
        }

        var phases = new JsonArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            phases.Add(new JsonObject
            {
                ["name"] = "sql_" + (i + 1).ToString(CultureInfo.InvariantCulture),
                ["elapsed_ms"] = Math.Round(entry.ElapsedMs, 3),
                ["rows_scanned"] = entry.RowsScanned,
            });
        }

        Console.WriteLine(new JsonObject
        {
            ["_debug"] = new JsonObject
            {
                ["sql_statement_count"] = entries.Count,
                ["elapsed_ms"] = elapsedMs,
                ["rows_scanned"] = rowsScanned,
                ["phases"] = phases,
                ["redaction"] = "SQL text and parameter values are omitted from --verbose debug output; use --profile for opt-in SQL diagnostics.",
            },
        }.ToJsonString(jsonOptions));
    }
}
