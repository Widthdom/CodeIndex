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
                sourceFileCount,
                RepresentsEntireSourceDatabase: true);
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
            ApplyPartialArchiveTrustMetadata(connection, transaction, cancellationToken);
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
            exportedFileCount,
            RepresentsEntireSourceDatabase: false);
    }

    private static ExportManifest ApplyImportedArchiveTrustMetadata(
        string databasePath,
        ExportManifest manifest,
        CancellationToken cancellationToken)
    {
        if (ManifestRepresentsEntireSourceDatabase(manifest))
            return manifest;

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection(CreateUnpooledConnectionString(databasePath));
        connection.Open();
        if (ArchiveTrustMetadataRequiresNormalization(connection))
        {
            using var transaction = connection.BeginTransaction();
            ApplyPartialArchiveTrustMetadata(connection, transaction, cancellationToken);
            transaction.Commit();
        }

        return NormalizeImportedArchiveTrustMetadata(
            manifest,
            ReadArchiveIncompleteReasons(connection));
    }

    private static bool ArchiveTrustMetadataRequiresNormalization(SqliteConnection connection)
    {
        using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = """
                SELECT EXISTS(
                    SELECT 1
                    FROM sqlite_master
                    WHERE type = 'table' AND name = 'codeindex_meta')
                """;
            if (Convert.ToInt64(tableCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                return true;
        }

        if (!string.Equals(
                ReadMetaString(connection, DbContext.IndexCompletenessMetaKey),
                "incomplete",
                StringComparison.Ordinal)
            || !HasPartialArchiveIncompleteReason(
                ReadMetaString(connection, DbContext.IndexIncompleteReasonsMetaKey))
            || ReadMetaString(connection, DbContext.UnknownExtensionFileCountMetaKey) != "0"
            || ReadMetaString(connection, DbContext.UnknownExtensionFilePathsMetaKey) != "[]"
            || !bool.TryParse(
                ReadMetaString(connection, DbContext.UnknownExtensionFilesTruncatedMetaKey),
                out var unknownFilesTruncated)
            || unknownFilesTruncated
            || ReadMetaString(connection, DbContext.UnknownExtensionExtensionCountsMetaKey) != "{}"
            || ReadMetaString(connection, DbContext.UnknownExtensionCategoryCountsMetaKey) != "{}"
            || ReadMetaString(connection, DbContext.UnknownExtensionGroupsMetaKey) != "[]")
        {
            return true;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM codeindex_meta
            WHERE key GLOB 'indexed_head_*'
               OR key GLOB 'last_index_run_*'
               OR key GLOB 'last_failed_index_run_*'
               OR key IN (
                   'commit_scoped_fresh_head_sha',
                   'last_full_scan_elapsed_ms',
                   'last_workspace_freshened_at')
            """;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool HasPartialArchiveIncompleteReason(string? rawReasons)
    {
        if (string.IsNullOrWhiteSpace(rawReasons)
            || Encoding.UTF8.GetByteCount(rawReasons) > MaxImportManifestBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                rawReasons,
                new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.String
                    && string.Equals(
                        item.GetString(),
                        PartialArchiveIncompleteReason,
                        StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ExportManifest NormalizeImportedArchiveTrustMetadata(
        ExportManifest manifest,
        string[]? persistedIncompleteReasons)
    {
        if (ManifestRepresentsEntireSourceDatabase(manifest))
            return manifest;

        return manifest with
        {
            IndexedHeadSha = null,
            IndexedHeadBranch = null,
            IndexedHeadTimestamp = null,
            UnknownExtensionFileCount = 0,
            UnknownExtensionFiles = null,
            UnknownExtensionFilesTruncated = false,
            UnknownExtensionFileSampleCount = 0,
            UnknownExtensionFileSampleTruncated = false,
            IndexComplete = false,
            IndexIncompleteReasons = persistedIncompleteReasons
                ?? [PartialArchiveIncompleteReason],
        };
    }

    private static bool ManifestRepresentsEntireSourceDatabase(ExportManifest manifest)
        => manifest.Scope?.RepresentsEntireSourceDatabase == true;

    private static void ApplyPartialArchiveTrustMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var ensureMeta = connection.CreateCommand())
        {
            ensureMeta.Transaction = transaction;
            ensureMeta.CommandText = """
                CREATE TABLE IF NOT EXISTS codeindex_meta (
                    key TEXT PRIMARY KEY NOT NULL,
                    value TEXT
                )
                """;
            ensureMeta.ExecuteNonQuery();
        }
        var incompleteReasonsJson = BuildPartialArchiveIncompleteReasonsJson(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM codeindex_meta
            WHERE key GLOB 'indexed_head_*'
               OR key GLOB 'last_index_run_*'
               OR key GLOB 'last_failed_index_run_*'
               OR key IN (
                   'commit_scoped_fresh_head_sha',
                   'last_full_scan_elapsed_ms',
                   'last_workspace_freshened_at');

            INSERT INTO codeindex_meta(key, value)
            VALUES (@indexCompletenessKey, 'incomplete')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO codeindex_meta(key, value)
            VALUES (@indexIncompleteReasonsKey, @indexIncompleteReasons)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO codeindex_meta(key, value)
            VALUES (@unknownExtensionFileCountKey, '0')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO codeindex_meta(key, value)
            VALUES (@unknownExtensionFilePathsKey, '[]')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO codeindex_meta(key, value)
            VALUES (@unknownExtensionFilesTruncatedKey, 'False')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO codeindex_meta(key, value)
            VALUES (@unknownExtensionExtensionCountsKey, '{}')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO codeindex_meta(key, value)
            VALUES (@unknownExtensionCategoryCountsKey, '{}')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO codeindex_meta(key, value)
            VALUES (@unknownExtensionGroupsKey, '[]')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        SqliteCommandPolicy.Add(command, "@indexCompletenessKey", DbContext.IndexCompletenessMetaKey);
        SqliteCommandPolicy.Add(command, "@indexIncompleteReasonsKey", DbContext.IndexIncompleteReasonsMetaKey);
        SqliteCommandPolicy.Add(command, "@indexIncompleteReasons", incompleteReasonsJson);
        SqliteCommandPolicy.Add(command, "@unknownExtensionFileCountKey", DbContext.UnknownExtensionFileCountMetaKey);
        SqliteCommandPolicy.Add(command, "@unknownExtensionFilePathsKey", DbContext.UnknownExtensionFilePathsMetaKey);
        SqliteCommandPolicy.Add(command, "@unknownExtensionFilesTruncatedKey", DbContext.UnknownExtensionFilesTruncatedMetaKey);
        SqliteCommandPolicy.Add(command, "@unknownExtensionExtensionCountsKey", DbContext.UnknownExtensionExtensionCountsMetaKey);
        SqliteCommandPolicy.Add(command, "@unknownExtensionCategoryCountsKey", DbContext.UnknownExtensionCategoryCountsMetaKey);
        SqliteCommandPolicy.Add(command, "@unknownExtensionGroupsKey", DbContext.UnknownExtensionGroupsMetaKey);
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string BuildPartialArchiveIncompleteReasonsJson(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var reasons = new List<string>(MaxArchiveIncompleteReasons);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rawReasons = ReadMetaString(connection, DbContext.IndexIncompleteReasonsMetaKey, transaction);
        if (!string.IsNullOrWhiteSpace(rawReasons)
            && Encoding.UTF8.GetByteCount(rawReasons) <= MaxImportManifestBytes)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    rawReasons,
                    new JsonDocumentOptions { MaxDepth = 4 });
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var totalChars = 0;
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || reasons.Count >= MaxArchiveIncompleteReasons - 1)
                            break;
                        var reason = item.GetString();
                        if (string.IsNullOrWhiteSpace(reason)
                            || reason.Length > MaxArchiveIncompleteReasonChars
                            || totalChars + reason.Length + PartialArchiveIncompleteReason.Length
                                > MaxArchiveIncompleteReasonsTotalChars
                            || !seen.Add(reason))
                        {
                            continue;
                        }
                        reasons.Add(reason);
                        totalChars += reason.Length;
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid legacy metadata is replaced by the stable partial-archive reason.
            }
        }

        if (seen.Add(PartialArchiveIncompleteReason))
            reasons.Add(PartialArchiveIncompleteReason);
        return JsonSerializer.Serialize(reasons);
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
