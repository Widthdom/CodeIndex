using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ExportImportCommandRunner
{
    private readonly record struct ArchivePathRedactionResult(
        ArchiveExportScopeResult Scope,
        bool Complete,
        string[] OmittedCategories);

    private static ArchivePathRedactionResult ApplyArchivePathRedaction(
        SqliteConnection connection,
        ArchiveExportScopeResult scope,
        bool redactPaths,
        CancellationToken cancellationToken)
    {
        if (!redactPaths)
            return new ArchivePathRedactionResult(scope, Complete: false, []);

        cancellationToken.ThrowIfCancellationRequested();
        var omittedCategories = new HashSet<string>(StringComparer.Ordinal);
        var projectRoot = ReadMetaString(connection, DbContext.IndexedProjectRootMetaKey);
        using (var transaction = connection.BeginTransaction())
        {
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                using var deleteRoot = connection.CreateCommand();
                deleteRoot.Transaction = transaction;
                deleteRoot.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                SqliteCommandPolicy.Add(deleteRoot, "@key", DbContext.IndexedProjectRootMetaKey);
                deleteRoot.ExecuteNonQuery();
                omittedCategories.Add("project_root");
            }

            RedactPersistedPathList(
                connection,
                transaction,
                DbContext.UnknownExtensionFilePathsMetaKey,
                "unknown_extension_files",
                omittedCategories);
            RedactPersistedUnknownExtensionGroups(
                connection,
                transaction,
                omittedCategories);
            RedactPersistedWorkspacePendingPaths(
                connection,
                transaction,
                omittedCategories);
            transaction.Commit();
        }

        var redactedScope = RedactArchiveScope(scope, omittedCategories);
        cancellationToken.ThrowIfCancellationRequested();
        using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM";
            vacuum.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ArchivePathRedactionResult(
            redactedScope,
            Complete: true,
            omittedCategories.Order(StringComparer.Ordinal).ToArray());
    }

    private static ArchiveExportScopeResult RedactArchiveScope(
        ArchiveExportScopeResult scope,
        HashSet<string> omittedCategories)
        => scope with
        {
            PathPatterns = RedactArchivePathValues(scope.PathPatterns, "scope.path", omittedCategories),
            ExcludePathPatterns = RedactArchivePathValues(scope.ExcludePathPatterns, "scope.exclude_path", omittedCategories),
            Projects = RedactArchivePathValues(scope.Projects, "scope.project", omittedCategories),
            Solution = RedactArchivePathValue(scope.Solution, "scope.solution", omittedCategories),
            ResolvedProjectPathPatterns = RedactArchivePathValues(
                scope.ResolvedProjectPathPatterns,
                "scope.resolved_project_path",
                omittedCategories),
        };

    private static string[] RedactArchivePathValues(
        IReadOnlyList<string> values,
        string category,
        HashSet<string> omittedCategories)
        => values.Select(value => RedactArchivePathValue(value, category, omittedCategories)!).ToArray();

    private static string? RedactArchivePathValue(
        string? value,
        string category,
        HashSet<string> omittedCategories)
    {
        if (value == null || !LooksLikeMachineAbsolutePath(value))
            return value;

        omittedCategories.Add(category);
        return RedactedArchivePath;
    }

    internal static bool LooksLikeMachineAbsolutePath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || trimmed[0] is '/' or '\\')
        {
            return true;
        }

        return trimmed.Length >= 3
            && char.IsAsciiLetter(trimmed[0])
            && trimmed[1] == ':'
            && trimmed[2] is '/' or '\\';
    }

    private static void RedactPersistedPathList(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string category,
        HashSet<string> omittedCategories)
    {
        var raw = ReadMetaString(connection, key, transaction);
        if (raw == null)
            return;

        var paths = JsonStringListCodec.Deserialize(raw);
        if (paths == null)
        {
            DeletePersistedPathMetadata(connection, transaction, key);
            omittedCategories.Add(category);
            return;
        }

        var redacted = RedactArchivePathValues(paths, category, omittedCategories);
        UpdatePersistedPathMetadata(
            connection,
            transaction,
            key,
            JsonStringListCodec.Serialize(redacted));
    }

    private static void RedactPersistedWorkspacePendingPaths(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HashSet<string> omittedCategories)
    {
        var raw = ReadMetaString(
            connection,
            DbContext.WorkspaceVerificationPendingPathsMetaKey,
            transaction);
        if (raw == null)
            return;

        var paths = JsonStringListCodec.Deserialize(raw);
        if (paths == null)
        {
            UpsertPersistedPathMetadata(
                connection,
                transaction,
                DbContext.WorkspaceVerificationPendingPathsMetaKey,
                JsonStringListCodec.Serialize([]));
            MarkWorkspacePendingPathCoverageIncomplete(connection, transaction);
            omittedCategories.Add("workspace_pending_paths");
            return;
        }

        var identityLost = paths.Any(LooksLikeMachineAbsolutePath);
        var redacted = RedactArchivePathValues(
            paths,
            "workspace_pending_paths",
            omittedCategories);
        UpdatePersistedPathMetadata(
            connection,
            transaction,
            DbContext.WorkspaceVerificationPendingPathsMetaKey,
            JsonStringListCodec.Serialize(redacted));
        if (identityLost)
            MarkWorkspacePendingPathCoverageIncomplete(connection, transaction);
    }

    private static void MarkWorkspacePendingPathCoverageIncomplete(
        SqliteConnection connection,
        SqliteTransaction transaction)
        => UpsertPersistedPathMetadata(
            connection,
            transaction,
            DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey,
            bool.FalseString);

    private static void RedactPersistedUnknownExtensionGroups(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HashSet<string> omittedCategories)
    {
        var raw = ReadMetaString(connection, DbContext.UnknownExtensionGroupsMetaKey, transaction);
        if (raw == null)
            return;

        var groups = UnknownExtensionClassifier.DeserializeGroups(raw);
        if (groups == null)
        {
            DeletePersistedPathMetadata(connection, transaction, DbContext.UnknownExtensionGroupsMetaKey);
            omittedCategories.Add("unknown_extension_groups");
            return;
        }

        foreach (var group in groups)
        {
            group.SamplePaths = RedactArchivePathValues(
                    group.SamplePaths,
                    "unknown_extension_groups",
                    omittedCategories)
                .ToList();
        }

        UpdatePersistedPathMetadata(
            connection,
            transaction,
            DbContext.UnknownExtensionGroupsMetaKey,
            UnknownExtensionClassifier.SerializeGroups(groups));
    }

    private static void UpdatePersistedPathMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE codeindex_meta SET value = @value WHERE key = @key";
        SqliteCommandPolicy.Add(update, "@value", value);
        SqliteCommandPolicy.Add(update, "@key", key);
        update.ExecuteNonQuery();
    }

    private static void UpsertPersistedPathMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO codeindex_meta(key, value)
            VALUES(@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        SqliteCommandPolicy.Add(upsert, "@key", key);
        SqliteCommandPolicy.Add(upsert, "@value", value);
        upsert.ExecuteNonQuery();
    }

    private static void DeletePersistedPathMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key)
    {
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
        SqliteCommandPolicy.Add(delete, "@key", key);
        delete.ExecuteNonQuery();
    }

    internal static bool TryValidateCompletedManifestPathRedaction(
        ExportManifest manifest,
        out string message)
    {
        if (!manifest.PathRedactionComplete)
        {
            message = string.Empty;
            return true;
        }

        if (manifest.ProjectRoot != null)
        {
            message = "path_redaction_complete requires project_root to be omitted";
            return false;
        }
        if (ContainsMachineAbsolutePath(manifest.UnknownExtensionFiles))
        {
            message = "path_redaction_complete cannot include absolute unknown_extension_files values";
            return false;
        }

        var scope = manifest.Scope;
        if (scope != null
            && (ContainsMachineAbsolutePath(scope.PathPatterns)
                || ContainsMachineAbsolutePath(scope.ExcludePathPatterns)
                || ContainsMachineAbsolutePath(scope.Projects)
                || ContainsMachineAbsolutePath(scope.ResolvedProjectPathPatterns)
                || scope.Solution != null && LooksLikeMachineAbsolutePath(scope.Solution)))
        {
            message = "path_redaction_complete cannot include absolute scope values";
            return false;
        }

        message = string.Empty;
        return true;
    }

    internal static bool TryValidateCompletedDatabasePathRedaction(
        ExportManifest manifest,
        SqliteConnection connection,
        out string message,
        CancellationToken cancellationToken)
    {
        if (!manifest.PathRedactionComplete)
        {
            message = string.Empty;
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ReadMetaString(connection, DbContext.IndexedProjectRootMetaKey) != null)
        {
            message = "path_redaction_complete does not match the embedded project root metadata";
            return false;
        }
        if (!TryValidatePersistedPathListRedaction(
                connection,
                DbContext.UnknownExtensionFilePathsMetaKey,
                "unknown-extension path",
                out _,
                out message)
            || !TryValidatePersistedUnknownExtensionGroupRedaction(connection, out message)
            || !TryValidatePersistedPathListRedaction(
                connection,
                DbContext.WorkspaceVerificationPendingPathsMetaKey,
                "workspace pending path",
                out var workspaceContainsRedactedValue,
                out message))
        {
            return false;
        }

        var workspaceIdentityOmitted = workspaceContainsRedactedValue
            || manifest.PathRedactionOmittedCategories?.Contains(
                "workspace_pending_paths",
                StringComparer.Ordinal) == true;
        if (workspaceIdentityOmitted
            && !string.Equals(
                ReadMetaString(connection, DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey),
                bool.FalseString,
                StringComparison.OrdinalIgnoreCase))
        {
            message = "redacted workspace pending paths require incomplete coverage metadata";
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        message = string.Empty;
        return true;
    }

    private static bool TryValidatePersistedPathListRedaction(
        SqliteConnection connection,
        string key,
        string description,
        out bool containsRedactedValue,
        out string message)
    {
        containsRedactedValue = false;
        var raw = ReadMetaString(connection, key);
        if (raw == null)
        {
            message = string.Empty;
            return true;
        }

        var paths = JsonStringListCodec.Deserialize(raw);
        if (paths == null)
        {
            message = $"path_redaction_complete does not match valid embedded {description} metadata";
            return false;
        }
        if (ContainsMachineAbsolutePath(paths))
        {
            message = $"path_redaction_complete does not match embedded absolute {description} metadata";
            return false;
        }

        containsRedactedValue = paths.Contains(RedactedArchivePath, StringComparer.Ordinal);
        message = string.Empty;
        return true;
    }

    private static bool TryValidatePersistedUnknownExtensionGroupRedaction(
        SqliteConnection connection,
        out string message)
    {
        var raw = ReadMetaString(connection, DbContext.UnknownExtensionGroupsMetaKey);
        if (raw == null)
        {
            message = string.Empty;
            return true;
        }

        var groups = UnknownExtensionClassifier.DeserializeGroups(raw);
        if (groups == null)
        {
            message = "path_redaction_complete does not match valid embedded unknown-extension group metadata";
            return false;
        }
        if (groups.Any(group => ContainsMachineAbsolutePath(group.SamplePaths)))
        {
            message = "path_redaction_complete does not match embedded absolute unknown-extension group metadata";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool ContainsMachineAbsolutePath(IEnumerable<string>? values)
        => values?.Any(LooksLikeMachineAbsolutePath) == true;
}
