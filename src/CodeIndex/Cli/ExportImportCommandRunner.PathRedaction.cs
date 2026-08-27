using System.Text.Json;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ExportImportCommandRunner
{
    private readonly record struct ArchivePathRedactionResult(
        ArchiveExportScopeResult Scope,
        string[] OmittedCategories);

    private static ArchivePathRedactionResult ApplyArchivePathRedaction(
        SqliteConnection connection,
        ArchiveExportScopeResult scope,
        bool redactPaths,
        CancellationToken cancellationToken)
    {
        if (!redactPaths)
            return new ArchivePathRedactionResult(scope, []);

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
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxImportManifestBytes)
            return;

        string[]? paths;
        try
        {
            paths = JsonSerializer.Deserialize<string[]>(raw);
        }
        catch (JsonException)
        {
            return;
        }
        if (paths == null || !paths.Any(LooksLikeMachineAbsolutePath))
            return;

        var redacted = RedactArchivePathValues(paths, category, omittedCategories);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE codeindex_meta SET value = @value WHERE key = @key";
        SqliteCommandPolicy.Add(update, "@value", JsonSerializer.Serialize(redacted));
        SqliteCommandPolicy.Add(update, "@key", key);
        update.ExecuteNonQuery();
    }
}
