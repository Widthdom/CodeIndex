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
            RedactPersistedPathList(
                connection,
                transaction,
                DbContext.WorkspaceVerificationPendingPathsMetaKey,
                "workspace_pending_paths",
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

    private static bool LooksLikeMachineAbsolutePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value[0] is '/' or '\\')
        {
            return true;
        }

        return value.Length >= 3
            && char.IsAsciiLetter(value[0])
            && value[1] == ':'
            && value[2] is '/' or '\\';
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
}
