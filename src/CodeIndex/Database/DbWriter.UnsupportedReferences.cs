using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Delete symbol_references for files whose language is no longer graph-supported.
    /// Prevents stale call edges from surviving after a language is removed from graph support.
    /// グラフ非対応になった言語のファイルから symbol_references を削除する。
    /// グラフ対応が外された言語の古いコールエッジが残存するのを防止する。
    /// </summary>
    /// <param name="supportedLanguages">Currently supported languages / 現在対応している言語</param>
    /// <returns>Number of stale reference rows deleted / 削除された古い参照行数</returns>
    public int PurgeUnsupportedReferences(IReadOnlyCollection<string> supportedLanguages)
    {
        if (supportedLanguages.Count == 0)
            return 0;

        using var transaction = !IsInTransaction() ? BeginTransaction() : null;
        var values = SnapshotSupportedLanguages(supportedLanguages);
        var inParams = BuildSupportedLanguageParameterNames(values.Count);
        var cmd = RentCommand(
            $@"
            DELETE FROM symbol_references
            WHERE file_id IN (
                SELECT f.id FROM files f
                WHERE f.lang IS NOT NULL
                  AND f.lang NOT IN ({string.Join(", ", inParams)})
            )",
            c => AddSupportedLanguageParameters(c, values.Count));
        try
        {
            BindSupportedLanguageParameterValues(cmd, values);
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
                InvalidateReferenceIdentityContractForMutation();
            transaction?.Commit();
            return deleted;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Delete all reference graph rows while preserving files, chunks, symbols, and issues.
    /// files / chunks / symbols / issues は残し、参照グラフ行だけを全削除する。
    /// </summary>
    public int PurgeAllReferences()
    {
        using var transaction = !IsInTransaction() ? BeginTransaction() : null;
        var referenceCmd = RentCommand("DELETE FROM symbol_references", static _ => { });
        int deletedReferences;
        try
        {
            deletedReferences = referenceCmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(referenceCmd);
        }
        if (deletedReferences > 0)
            InvalidateReferenceIdentityContractForMutation();

        var lineCmd = RentCommand("DELETE FROM reference_lines", static _ => { });
        try
        {
            lineCmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(lineCmd);
        }

        transaction?.Commit();
        return deletedReferences;
    }

    private static IReadOnlyList<string> SnapshotSupportedLanguages(IReadOnlyCollection<string> supportedLanguages)
        => supportedLanguages as IReadOnlyList<string> ?? supportedLanguages.ToList();

    private static List<string> BuildSupportedLanguageParameterNames(int count)
    {
        SqliteDynamicSql.EnsureParameterBudget(count, "supported language filters");
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
            names.Add(SqliteDynamicSql.BuildParameterName("lang", i));
        return names;
    }

    private static void AddSupportedLanguageParameters(SqliteCommand cmd, int count)
    {
        SqliteDynamicSql.EnsureParameterBudget(count, "supported language filters");
        for (var i = 0; i < count; i++)
            cmd.Parameters.Add(SqliteDynamicSql.BuildParameterName("lang", i), SqliteType.Text);
    }

    private static void BindSupportedLanguageParameterValues(SqliteCommand cmd, IReadOnlyList<string> supportedLanguages)
    {
        for (var i = 0; i < supportedLanguages.Count; i++)
            cmd.Parameters[SqliteDynamicSql.BuildParameterName("lang", i)].Value = supportedLanguages[i];
    }
}
