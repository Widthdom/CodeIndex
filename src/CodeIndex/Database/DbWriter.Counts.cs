namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Get total counts for the summary output.
    /// サマリー出力用の合計件数を取得する。
    /// </summary>
    public (long files, long chunks, long symbols, long references) GetCounts()
    {
        CountsReadForTesting?.Invoke();
        long files = ExecuteScalar("SELECT COUNT(*) FROM files");
        long chunks = ExecuteScalar("SELECT COUNT(*) FROM chunks");
        long symbols = ExecuteScalar("SELECT COUNT(*) FROM symbols");
        long references = ExecuteScalar("SELECT COUNT(*) FROM symbol_references");
        return (files, chunks, symbols, references);
    }

    /// <summary>
    /// Return whether a scoped update could leave existing file rows untouched.
    /// scoped update が既存 file 行を未更新のまま残し得るかを返す。
    /// </summary>
    internal bool HasIndexedFiles()
        => ExecuteScalar("SELECT EXISTS(SELECT 1 FROM files LIMIT 1)") != 0;

    private long ExecuteScalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
