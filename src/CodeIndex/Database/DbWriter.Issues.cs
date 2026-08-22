using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Insert file validation issues.
    /// ファイル検証問題を挿入する。
    /// </summary>
    public void InsertIssues(long fileId, IReadOnlyList<CodeIndex.Models.FileIssue> issues)
        => InsertIssues(fileId, issues, deleteExisting: true);

    /// <summary>
    /// Insert validation issues for a newly-created file row that cannot have existing issues.
    /// 既存 issue が存在しない新規ファイル行向けに検証問題を挿入する。
    /// </summary>
    public void InsertIssuesForNewFile(long fileId, IReadOnlyList<CodeIndex.Models.FileIssue> issues)
        => InsertIssues(fileId, issues, deleteExisting: false);

    private void InsertIssues(long fileId, IReadOnlyList<CodeIndex.Models.FileIssue> issues, bool deleteExisting)
    {
        if (deleteExisting)
        {
            // Always delete existing issues — if the file is now clean, old issues must be removed.
            // 常に既存問題を削除 — ファイルが修正済みなら古い問題を残さない。
            var delCmd = RentCommand(
                "DELETE FROM file_issues WHERE file_id = @fid",
                static c => c.Parameters.Add("@fid", SqliteType.Integer));
            try
            {
                delCmd.Parameters["@fid"].Value = fileId;
                delCmd.ExecuteNonQuery();
            }
            finally
            {
                ReleaseCommand(delCmd);
            }
        }

        if (issues.Count == 0) return;
        if (issues.Count == 1)
        {
            InsertSingleIssue(fileId, issues[0]);
            return;
        }

        int rowsPerStatement = IsInTransaction()
            ? GetRowsPerCallerTransactionInsertStatement(columnCount: 6)
            : GetRowsPerInsertStatement(columnCount: 6);
        for (int i = 0; i < issues.Count; i += rowsPerStatement)
        {
            int end = Math.Min(i + rowsPerStatement, issues.Count);
            InsertIssueBatch(fileId, issues, i, end);
        }
    }

    private void InsertSingleIssue(long fileId, CodeIndex.Models.FileIssue issue)
    {
        var cmd = RentCommand(
            "INSERT INTO file_issues (file_id, kind, line, message, origin, severity) VALUES (@fid, @kind, @line, @message, @origin, @severity)",
            static c =>
            {
                c.Parameters.Add("@fid", SqliteType.Integer);
                c.Parameters.Add("@kind", SqliteType.Text);
                c.Parameters.Add("@line", SqliteType.Integer);
                c.Parameters.Add("@message", SqliteType.Text);
                c.Parameters.Add("@origin", SqliteType.Text);
                c.Parameters.Add("@severity", SqliteType.Text);
            });
        try
        {
            var pFid = cmd.Parameters["@fid"];
            var pKind = cmd.Parameters["@kind"];
            var pLine = cmd.Parameters["@line"];
            var pMessage = cmd.Parameters["@message"];
            var pOrigin = cmd.Parameters["@origin"];
            var pSeverity = cmd.Parameters["@severity"];

            pFid.Value = fileId;
            pKind.Value = issue.Kind;
            pLine.Value = issue.Line;
            pMessage.Value = issue.Message;
            pOrigin.Value = issue.Origin ?? (object)DBNull.Value;
            pSeverity.Value = issue.Severity ?? (object)DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private void InsertIssueBatch(long fileId, IReadOnlyList<CodeIndex.Models.FileIssue> issues, int start, int end)
    {
        using var cmd = _conn.CreateCommand();
        var sql = CreateBatchSqlBuilder(end - start, estimatedCharsPerRow: 96);
        sql.Append("INSERT INTO file_issues (file_id, kind, line, message, origin, severity) VALUES ");
        var sqlParameterIndex = 0;
        for (int j = start; j < end; j++)
        {
            if (j > start)
                sql.Append(", ");
            AppendBatchParameterTuple(sql, ref sqlParameterIndex, columnCount: 6);
        }

        var parameterIndex = 0;
        for (int j = start; j < end; j++)
        {
            var issue = issues[j];
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer).Value = fileId;
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text).Value = issue.Kind;
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer).Value = issue.Line;
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text).Value = issue.Message;
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text).Value = issue.Origin ?? (object)DBNull.Value;
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text).Value = issue.Severity ?? (object)DBNull.Value;
        }

        cmd.CommandText = sql.ToString();
        ReportBatchStatementForTesting("insert_issues", end - start, end - start);
        cmd.ExecuteNonQuery();
    }
}
