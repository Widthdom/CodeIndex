using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    // Regex helpers for SQL procedure body scanning / SQL プロシージャ本体走査用の正規表現ヘルパー
    private static readonly Regex SqlGoSeparatorRegex = new(
        @"^\s*GO\s*(?:;[\s;]*)?(?:--.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Only close a SQL proc body when the next top-level statement looks like another proc-like
    // header (`CREATE|ALTER|DROP PROCEDURE|PROC|FUNCTION|TRIGGER`, optionally with `OR REPLACE` for
    // PostgreSQL or `OR ALTER` for T-SQL / SQL Server 2016+). Body-internal `CREATE TABLE` /
    // `ALTER TABLE` / `GRANT` / `USE` etc. must not prematurely close the enclosing procedure body.
    // The `OR REPLACE` / `OR ALTER` alternation must match the CREATE-side symbol regex above so a
    // `CREATE OR ALTER PROCEDURE` sibling actually terminates the previous body range. See issue #429.
    // 次のトップレベル文が別の proc 系ヘッダ（`CREATE|ALTER|DROP` + `PROCEDURE|PROC|FUNCTION|TRIGGER`、
    // PostgreSQL の `OR REPLACE` / T-SQL・SQL Server 2016+ の `OR ALTER` 付きも許容）だった場合のみ
    // SQL の proc 本体を閉じる。本体内の `CREATE TABLE` / `ALTER TABLE` / `GRANT` / `USE` などで
    // 先走って閉じないこと。`OR REPLACE` / `OR ALTER` の分岐は上の CREATE 側シンボル正規表現と揃え、
    // `CREATE OR ALTER PROCEDURE` の隣接宣言でも前の body 範囲を確実に終端させる。issue #429 参照。
    private static readonly Regex SqlTopLevelDdlStartRegex = new(
        @"^\s*(?:CREATE|ALTER|DROP)\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:PROCEDURE|PROC|FUNCTION|TRIGGER)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Dollar-quoted body tags: `$$` or `$tagname$` (PostgreSQL). Tag must be empty or an identifier.
    // Dollar-quoted の本体タグ: `$$` または `$タグ名$`（PostgreSQL）。タグは空か識別子のみ。
    private static readonly Regex SqlDollarTagRegex = new(
        @"\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$",
        RegexOptions.Compiled);

    /// <summary>
    /// Resolve the body range of a SQL `CREATE|ALTER PROCEDURE|FUNCTION|TRIGGER` symbol.
    /// Closes the body at: balanced dollar-quoted (`$$ ... $$`), `GO` batch separator at line start,
    /// a new top-level DDL statement (`CREATE`/`ALTER`/`DROP`/...) at line start, or end-of-file.
    /// Multi-line scanning respects SQL string literals (`'...'` / `"..."`) and line/block comments
    /// so terminators embedded in comments or strings do not prematurely close the body.
    /// Body boundaries are best-effort — they only need to contain calls inside the procedure for
    /// ReferenceExtractor's container attribution, not reconstruct the exact parser-level body.
    /// See issue #429.
    /// SQL の `CREATE|ALTER PROCEDURE|FUNCTION|TRIGGER` シンボルの本体範囲を求める。
    /// 本体は、`$$ ... $$` 等のドル引用の閉じ、行頭の `GO` バッチ区切り、行頭の新たなトップレベル DDL
    /// （`CREATE`/`ALTER`/`DROP`/...）、または EOF で閉じる。文字列リテラル（`'...'` / `"..."`）と
    /// 行/ブロックコメントは尊重するため、これらの中に入った終端語で誤って閉じない。
    /// 本体境界は ReferenceExtractor のコンテナ帰属のためにプロシージャ内部の呼び出しを包含できれば
    /// 十分で、パーサレベルの正確な本体を再構築する必要はない。issue #429 参照。
    /// </summary>
    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindSqlProcBodyRange(string[] lines, int startIndex)
    {
        int bodyStartLine = startIndex + 1;
        int endLine = startIndex + 1;
        string? openDollarTag = null;
        int blockCommentDepth = 0;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var raw = lines[i];

            if (openDollarTag != null)
            {
                // Inside a dollar-quoted body; look for the matching close tag on any column.
                // ドル引用ボディ内。どの列にあっても閉じタグを探す。
                if (raw.IndexOf(openDollarTag, StringComparison.Ordinal) >= 0)
                {
                    openDollarTag = null;
                    endLine = i + 1;
                    return (endLine, bodyStartLine, endLine);
                }
                endLine = i + 1;
                continue;
            }

            var masked = MaskSqlLineForBodyScan(raw, ref blockCommentDepth);

            // Detect an unpaired dollar-quote opening on this line. Once a tag opens, only the same
            // tag text can close it; different tags inside the body are literal text in PostgreSQL
            // and must not terminate the procedure body early.
            // 同一行でペアにならない dollar-quote 開きを検出する。いったん開いたら同じタグ文字列だけで閉じる。
            // 本体内の異なるタグは PostgreSQL ではリテラルなので、プロシージャ本体を早期終了させない。
            openDollarTag = FindUnclosedSqlDollarTag(masked);
            if (openDollarTag != null)
            {
                endLine = i + 1;
                continue;
            }

            if (i > startIndex)
            {
                // Use `masked` for GO detection too so a bare `GO` appearing inside a multi-line
                // block comment does not prematurely close the body (the mask blanks out
                // comment-interior content). See issue #429 follow-up.
                // `GO` 判定にも `masked` を使い、複数行ブロックコメント内の `GO` 単独行で本体を
                // 早期終了させない（マスクでコメント内部は空白化される）。issue #429 追補参照。
                if (SqlGoSeparatorRegex.IsMatch(masked))
                {
                    // `GO` is a T-SQL batch separator that is not part of the body; close at the
                    // previous line so the `GO` line itself is outside the procedure.
                    // `GO` は本体の一部ではない T-SQL のバッチ区切り。前行で本体を閉じ、`GO` 行自体は
                    // プロシージャの外に置く。
                    return (i, bodyStartLine, i);
                }

                if (SqlTopLevelDdlStartRegex.IsMatch(masked))
                {
                    // A new top-level DDL statement on the next line always closes the previous
                    // procedure's body (even without `GO`).
                    // 次の行に新しいトップレベル DDL が来たら、`GO` が無くても前のプロシージャ本体は
                    // ここで閉じる。
                    return (i, bodyStartLine, i);
                }
            }

            endLine = i + 1;
        }

        return (endLine, bodyStartLine, endLine);
    }

    private static string? FindUnclosedSqlDollarTag(string maskedLine)
    {
        int searchFrom = 0;

        while (searchFrom < maskedLine.Length)
        {
            var open = SqlDollarTagRegex.Match(maskedLine, searchFrom);
            if (!open.Success)
                return null;

            string tag = open.Value;
            int closeIndex = maskedLine.IndexOf(tag, open.Index + open.Length, StringComparison.Ordinal);
            if (closeIndex < 0)
                return tag;

            searchFrom = closeIndex + tag.Length;
        }

        return null;
    }

    /// <summary>
    /// Strip SQL line comments (`--`), block comments (`/* ... */`, including multi-line and
    /// PostgreSQL-style nested `/* /* ... */ ... */`), and string literals (`'...'` / `"..."`)
    /// from a single line so body-terminator checks do not trip on text inside comments or
    /// strings. Bracket identifiers (`[name]`) and backtick identifiers (`` `name` ``) are left
    /// untouched since they never contain SQL tokens.
    /// `blockCommentDepth` is threaded across lines by the caller so a `/* ... */` block opened on
    /// one line continues to mask terminators like bare `GO` / `CREATE` appearing on subsequent
    /// lines until all open `/*` are balanced. PostgreSQL allows nested block comments; T-SQL /
    /// MySQL / Oracle do not, but using depth here is strictly safer for the dialects that do not
    /// (every outer `*/` still closes when depth returns to 0). See issue #429 follow-up.
    /// SQL の行コメント（`--`）、ブロックコメント（`/* ... */`、複数行にまたがるものと PostgreSQL 風の
    /// ネスト `/* /* ... */ ... */` を含む）、および文字列リテラル（`'...'` / `"..."`）を除去して、
    /// コメントや文字列中の語で本体終端が誤検出されないようにする。角括弧識別子 `[name]` と
    /// バッククォート識別子 `` `name` `` はそのまま残す（SQL トークンを含まないため）。
    /// `blockCommentDepth` は呼び出し側で行間に持ち越し、ある行で開いた `/* ... */` がすべての `/*`
    /// と均衡するまで、後続行の `GO` / `CREATE` のような終端語をマスクし続ける。PostgreSQL は
    /// ブロックコメントのネストを許容する一方、T-SQL / MySQL / Oracle は許容しないが、ここで depth を
    /// 使っても後者では単に外側の `*/` で depth が 0 に戻って閉じるだけなので、厳密に safer。
    /// issue #429 追補参照。
    /// </summary>
    private static string MaskSqlLineForBodyScan(string line, ref int blockCommentDepth)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        var sb = new StringBuilder(line.Length);
        bool inSingle = false;
        bool inDouble = false;
        int i = 0;

        while (i < line.Length)
        {
            if (blockCommentDepth > 0)
            {
                // Inside a (possibly nested) block comment. Look for the next `/*` (increases depth)
                // or `*/` (decreases depth), whichever comes first. Blank every column until we
                // either close back to depth 0 or hit end of line.
                // ブロックコメント内（ネスト可）。次に来る `/*`（深さ増）か `*/`（深さ減）のうち早い方を探し、
                // depth が 0 に戻るか行末に到達するまで各列を空白化する。
                int open = line.IndexOf("/*", i, StringComparison.Ordinal);
                int close = line.IndexOf("*/", i, StringComparison.Ordinal);

                if (close < 0 && open < 0)
                {
                    for (int k = i; k < line.Length; k++)
                        sb.Append(' ');
                    return sb.ToString();
                }

                if (close >= 0 && (open < 0 || close < open))
                {
                    for (int k = i; k <= close + 1; k++)
                        sb.Append(' ');
                    blockCommentDepth--;
                    i = close + 2;
                    continue;
                }

                // `/*` comes first (or is tied — but `close < open` already handles the tie in favor
                // of `*/`, so here we know `open <= close` strictly and `/*` is the next token).
                // `/*` が先に現れる（`close < open` の場合は close 優先で既に分岐済みなので、ここでは
                // `open <= close` かつ `/*` が次のトークン）。
                for (int k = i; k <= open + 1; k++)
                    sb.Append(' ');
                blockCommentDepth++;
                i = open + 2;
                continue;
            }

            char c = line[i];

            if (!inSingle && !inDouble)
            {
                if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    for (int k = i; k < line.Length; k++)
                        sb.Append(' ');
                    return sb.ToString();
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    // Open a block comment; let the `blockCommentDepth > 0` branch close it on this
                    // or a later line.
                    // ブロックコメントを開始する。閉じ処理は `blockCommentDepth > 0` 分岐で同じ行か
                    // 後続行のどちらかが担当する。
                    sb.Append(' ');
                    sb.Append(' ');
                    blockCommentDepth = 1;
                    i += 2;
                    continue;
                }
                if (c == '\'')
                {
                    inSingle = true;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    inDouble = true;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            else if (inSingle)
            {
                if (c == '\'' && i + 1 < line.Length && line[i + 1] == '\'')
                {
                    sb.Append(' ');
                    sb.Append(' ');
                    i += 2;
                    continue;
                }
                if (c == '\'')
                {
                    inSingle = false;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                sb.Append(' ');
                i++;
            }
            else
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append(' ');
                    sb.Append(' ');
                    i += 2;
                    continue;
                }
                if (c == '"')
                {
                    inDouble = false;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                sb.Append(' ');
                i++;
            }
        }

        return sb.ToString();
    }

}
