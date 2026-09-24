using System.Globalization;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    // Only unambiguous data-statement starts qualify: UPDATE SET (MERGE / ON CONFLICT)
    // and UPDATE column = value (ON DUPLICATE KEY) are continuations, not boundaries.
    // MERGE / ON CONFLICT の UPDATE SET や ON DUPLICATE KEY の UPDATE column = value は
    // 継続句なので、独立した DML 文の開始だけを境界にする。
    private static readonly Regex IndependentDataStatementStartRegex = new(
        $@"^\s*(?:UPDATE\s+{QualifiedIdentifierNoCapturePattern}\s+SET\b|INSERT\s+(?:INTO\s+)?{QualifiedIdentifierNoCapturePattern}|DELETE\s+FROM\s+{QualifiedIdentifierNoCapturePattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsBatchSeparator(string line)
    {
        var text = line.AsSpan().Trim();
        if (text.Length < 2 || !text[..2].Equals("GO", StringComparison.OrdinalIgnoreCase))
            return false;
        if (text.Length == 2)
            return true;
        return char.IsWhiteSpace(text[2])
            && int.TryParse(text[2..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count > 0;
    }

    private static bool ShouldFlushDataStatementPrefixAtLineBoundary(string prefix, string nextLine)
    {
        var start = SkipWhitespaceAhead(prefix, 0);
        // Keep compound statements (WITH, MERGE, CREATE, etc.) intact. Their later
        // lines can depend on earlier clauses even outside parentheses.
        // WITH / MERGE / CREATE などは前の句が後続行の解析に必要なので維持する。
        if (!IsKeywordAt(prefix, start, "INSERT")
            && !IsKeywordAt(prefix, start, "UPDATE")
            && !IsKeywordAt(prefix, start, "DELETE")
            && !IsKeywordAt(prefix, start, "SET"))
        {
            return false;
        }

        if (!IndependentDataStatementStartRegex.IsMatch(nextLine))
            return false;

        // The input is already comment/string masked. Quoted identifiers must still
        // be skipped so parentheses in names cannot create a false statement boundary.
        // 入力の comment / string はマスク済み。引用識別子内の括弧も境界に数えない。
        var depth = 0;
        for (var index = start; index < prefix.Length; index++)
        {
            var value = prefix[index];
            if (value is '[' or '"' or '`')
            {
                var closing = value == '[' ? ']' : value;
                var closed = false;
                while (++index < prefix.Length)
                {
                    if (prefix[index] != closing)
                        continue;
                    if (index + 1 < prefix.Length && prefix[index + 1] == closing)
                    {
                        index++;
                        continue;
                    }
                    closed = true;
                    break;
                }
                if (!closed)
                    return false;
            }
            else if (value == '(')
            {
                depth++;
            }
            else if (value == ')' && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }
}
