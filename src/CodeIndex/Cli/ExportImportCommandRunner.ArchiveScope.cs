using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Archives;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ExportImportCommandRunner
{
    private static ArchiveExportScopeResult ApplyArchiveScope(
        SqliteConnection connection,
        ArchiveExportOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFileCount = ReadTableCount(connection, "files", cancellationToken);
        var projectPathPatterns = Array.Empty<string>();
        if (options.Projects.Count > 0)
        {
            var projectRoot = ReadMetaString(connection, DbContext.IndexedProjectRootMetaKey);
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new InvalidOperationException("archive project filters require indexed_project_root metadata");
            projectPathPatterns = SolutionProjectResolver
                .ResolveProjectDirectoryGlobs(projectRoot, options.Projects, options.Solution)
                .ToArray();
        }

        var effectivePathPatterns = options.PathPatterns.Concat(projectPathPatterns).ToArray();
        var scoped =
            !string.IsNullOrWhiteSpace(options.Lang)
            || effectivePathPatterns.Length > 0
            || options.ExcludePathPatterns.Count > 0
            || options.ExcludeTests;
        if (!scoped)
        {
            return new ArchiveExportScopeResult(
                false,
                options.Lang,
                options.PathPatterns,
                options.ExcludePathPatterns,
                options.Projects,
                options.Solution,
                options.ExcludeTests,
                projectPathPatterns,
                sourceFileCount,
                sourceFileCount);
        }

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON";
            foreignKeys.ExecuteNonQuery();
        }

        using (var transaction = connection.BeginTransaction())
        {
            using var keepCommand = connection.CreateCommand();
            keepCommand.Transaction = transaction;
            keepCommand.CommandText = """
                CREATE TEMP TABLE archive_scope_files(id INTEGER PRIMARY KEY);
                INSERT INTO archive_scope_files(id)
                SELECT f.id
                FROM files f
                WHERE 1 = 1
                """;
            if (!string.IsNullOrWhiteSpace(options.Lang))
                keepCommand.CommandText += " AND f.lang = @lang";
            if (effectivePathPatterns.Length > 0)
            {
                var pathPredicates = new List<string>(effectivePathPatterns.Length);
                for (var i = 0; i < effectivePathPatterns.Length; i++)
                    pathPredicates.Add(DbReader.BuildPathFilterPredicate("f", "archivePath", i, effectivePathPatterns[i]));
                keepCommand.CommandText += " AND (" + string.Join(" OR ", pathPredicates) + ")";
            }
            for (var i = 0; i < options.ExcludePathPatterns.Count; i++)
                keepCommand.CommandText += $" AND NOT {DbReader.BuildPathFilterPredicate("f", "archiveExcludePath", i, options.ExcludePathPatterns[i])}";
            if (options.ExcludeTests)
                keepCommand.CommandText += $" AND NOT {DbReader.TestPathCondition}";
            if (!string.IsNullOrWhiteSpace(options.Lang))
                SqliteCommandPolicy.Add(keepCommand, "@lang", options.Lang);
            DbReader.AddPathFilterParameterSet(keepCommand, "archivePath", effectivePathPatterns);
            DbReader.AddPathFilterParameterSet(keepCommand, "archiveExcludePath", options.ExcludePathPatterns);
            keepCommand.ExecuteNonQuery();

            using var pruneCommand = connection.CreateCommand();
            pruneCommand.Transaction = transaction;
            pruneCommand.CommandText = """
                DELETE FROM symbol_reference_candidates
                WHERE reference_id IN (
                    SELECT r.id
                    FROM symbol_references r
                    WHERE r.file_id NOT IN (SELECT id FROM archive_scope_files)
                )
                   OR symbol_id IN (
                    SELECT s.id
                    FROM symbols s
                    WHERE s.file_id NOT IN (SELECT id FROM archive_scope_files)
                );

                UPDATE symbol_references
                SET source_symbol_id = NULL
                WHERE source_symbol_id IN (
                    SELECT s.id
                    FROM symbols s
                    WHERE s.file_id NOT IN (SELECT id FROM archive_scope_files)
                );

                UPDATE symbol_references
                SET target_symbol_id = NULL
                WHERE target_symbol_id IN (
                    SELECT s.id
                    FROM symbols s
                    WHERE s.file_id NOT IN (SELECT id FROM archive_scope_files)
                );

                DELETE FROM files
                WHERE id NOT IN (SELECT id FROM archive_scope_files);

                DELETE FROM symbol_reference_candidates
                WHERE reference_id NOT IN (SELECT id FROM symbol_references)
                   OR symbol_id NOT IN (SELECT id FROM symbols);

                DROP TABLE archive_scope_files;
                """;
            pruneCommand.ExecuteNonQuery();
            DbWriter.RebuildRetainedReferenceGraph(connection, transaction, cancellationToken);
            transaction.Commit();
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (var foreignKeyCheck = connection.CreateCommand())
        {
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check";
            using var reader = foreignKeyCheck.ExecuteReader();
            if (reader.Read())
                throw new InvalidDataException("scoped archive snapshot failed SQLite foreign-key validation");
        }

        using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM";
            vacuum.ExecuteNonQuery();
        }

        var exportedFileCount = ReadTableCount(connection, "files", cancellationToken);
        return new ArchiveExportScopeResult(
            true,
            options.Lang,
            options.PathPatterns,
            options.ExcludePathPatterns,
            options.Projects,
            options.Solution,
            options.ExcludeTests,
            projectPathPatterns,
            sourceFileCount,
            exportedFileCount);
    }

    private static bool TryValidateArchiveScopeValues(
        IReadOnlyList<string> pathPatterns,
        IReadOnlyList<string> excludePathPatterns,
        IReadOnlyList<string> projects,
        string? solution,
        out string message)
    {
        var values = pathPatterns.Concat(excludePathPatterns).Concat(projects).ToList();
        if (solution != null)
            values.Add(solution);
        if (values.Count > MaxArchiveScopeValues)
        {
            message = $"archive export accepts at most {MaxArchiveScopeValues} scope values";
            return false;
        }

        var totalChars = 0;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                message = "archive scope values must not be empty";
                return false;
            }
            if (value.Length > MaxArchiveScopeValueChars)
            {
                message = $"archive scope values must not exceed {MaxArchiveScopeValueChars} characters";
                return false;
            }
            totalChars += value.Length;
            if (totalChars > MaxArchiveScopeTotalChars)
            {
                message = $"archive scope values exceed the combined limit of {MaxArchiveScopeTotalChars} characters";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static ImportDestinationDeltaResult BuildImportDestinationDelta(
        string destinationDbPath,
        string importedDbPath,
        string archivePath,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(destinationDbPath))
        {
            return new ImportDestinationDeltaResult(
                DestinationExists: false,
                Comparable: false,
                Status: "destination_missing",
                Comparison: null,
                Message: "destination database does not exist; the archive would create it");
        }

        var snapshotDirectory = Path.GetDirectoryName(importedDbPath)
            ?? throw new InvalidOperationException("import comparison directory could not be resolved");
        var destinationSnapshotPath = Path.Combine(snapshotDirectory, "destination-codeindex.db");
        try
        {
            try
            {
                using (var source = BoundedFile.OpenReadForIndexContent(destinationDbPath))
                {
                    if (source.Length > MaxImportDatabaseBytes)
                    {
                        return new ImportDestinationDeltaResult(
                            DestinationExists: true,
                            Comparable: false,
                            Status: "destination_too_large",
                            Comparison: null,
                            Message: $"destination database exceeds the comparison limit of {ConsoleUi.FormatBytes(MaxImportDatabaseBytes)}");
                    }

                    Span<byte> header = stackalloc byte[16];
                    if (source.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
                    {
                        return new ImportDestinationDeltaResult(
                            DestinationExists: true,
                            Comparable: false,
                            Status: "destination_unreadable",
                            Comparison: null,
                            Message: "destination database could not be compared from a non-mutating snapshot: file header is not SQLite format 3");
                    }
                }

                CreateDatabaseSnapshot(destinationDbPath, destinationSnapshotPath, cancellationToken);
            }
            catch (Exception ex) when (ex is SqliteException or CodeIndexException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new ImportDestinationDeltaResult(
                    DestinationExists: true,
                    Comparable: false,
                    Status: "destination_unreadable",
                    Comparison: null,
                    Message: $"destination database could not be compared from a non-mutating snapshot: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            }

            if (!DbContext.TryValidateExistingCodeIndexDb(
                    destinationSnapshotPath,
                    requireWritable: false,
                    requireSupportedUserVersion: false,
                    out var validationMessage,
                    out _,
                    out _,
                    cancellationToken))
            {
                return new ImportDestinationDeltaResult(
                    DestinationExists: true,
                    Comparable: false,
                    Status: "destination_unreadable",
                    Comparison: null,
                    Message: $"destination database could not be compared from a non-mutating snapshot: {validationMessage}");
            }

            var comparison = DiffCommandRunner.CompareDatabases(
                destinationSnapshotPath,
                importedDbPath,
                limit,
                offset,
                detailed: true,
                cancellationToken,
                destinationDbPath,
                archivePath,
                emitCursorMetadata: false);
            var comparable = comparison.Status != "schema_mismatch";
            return new ImportDestinationDeltaResult(
                DestinationExists: true,
                Comparable: comparable,
                Status: comparable ? "compared" : "schema_mismatch",
                Comparison: comparison,
                Message: comparable
                    ? "destination database was compared from a non-mutating snapshot with the validated archive snapshot"
                    : "destination and archive schema versions differ");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(destinationSnapshotPath, "import destination comparison snapshot");
            DeleteSqliteSidecars(destinationSnapshotPath, "import destination comparison snapshot sidecar");
        }
    }

    private static string FormatDestinationDeltaSummary(ImportDestinationDeltaResult destinationDelta)
    {
        if (!destinationDelta.Comparable || destinationDelta.Comparison is not { } comparison)
            return $"; {destinationDelta.Message}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"; destination delta: files {comparison.Summary.FileCountDelta:+#;-#;0}, symbols {comparison.Summary.SymbolCountDelta:+#;-#;0}, references {comparison.Summary.ReferenceCountDelta:+#;-#;0}");
    }

}
