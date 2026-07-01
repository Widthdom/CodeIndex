using System.Globalization;
using System.Text;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private static void AddOversizeContentIssues(List<FileIssue> issues, string relativePath, string content, bool? hasOversizeLine)
    {
        // line_too_long — surface the chunk/symbol/reference skip path that
        // triggers when a single physical line exceeds ChunkSplitter.MaxLineLength
        // (e.g. 1 MB minified `.min.js`, base64-encoded asset). The matching
        // guards in ChunkSplitter, SymbolExtractor, and ReferenceExtractor
        // already return empty for such files; this FileIssue lets callers
        // diagnose the silent stall the issue was filed for. Closes #1542.
        // line_too_long — 単一物理行が ChunkSplitter.MaxLineLength を超える
        // ファイル (例: 1 MB minified .min.js、base64 ペイロード) で発生する
        // chunk/symbol/reference スキップ経路を可視化する。ChunkSplitter /
        // SymbolExtractor / ReferenceExtractor 側の同等ガードはすでに空を返す
        // ため、本 FileIssue は issue 起票時の「無音停止」を切り分けやすくする
        // 観測点を提供する。Closes #1542.
        if (hasOversizeLine ?? ChunkSplitter.HasOversizeLine(content))
        {
            var longLine = FindOversizeLine(content, ChunkSplitter.MaxLineLength);
            if (longLine > 0)
            {
                issues.Add(new FileIssue
                {
                    Path = relativePath,
                    Kind = "line_too_long",
                    Line = longLine,
                    Message = $"Line {longLine} exceeds {ChunkSplitter.MaxLineLength}-char cap; chunks/symbols/references skipped",
                });
            }
        }

        var longFtsTokenLine = FindOversizeFtsTokenLine(content, CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength);
        if (longFtsTokenLine > 0)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "fts_token_too_long",
                Line = longFtsTokenLine,
                Message = $"Line {longFtsTokenLine} contains an FTS5 unicode61 token longer than {CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength} characters; that token is not searchable through FTS",
            });
        }
    }

    /// <summary>
    /// Return the 1-based number of the first line whose length exceeds
    /// <paramref name="maxLineLength"/>, or 0 when none. Assumes `\n` is the
    /// only line separator (callers normalize CRLF). Used by ValidateContent
    /// to attach a precise line number to the `line_too_long` FileIssue.
    /// </summary>
    private static int FindOversizeLine(string content, int maxLineLength)
    {
        if (string.IsNullOrEmpty(content))
            return 0;
        int lineNumber = 1;
        int lineLen = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                lineNumber++;
                lineLen = 0;
                continue;
            }
            lineLen++;
            if (lineLen > maxLineLength)
                return lineNumber;
        }
        return 0;
    }

    private static int FindOversizeFtsTokenLine(string content, int maxTokenLength)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxTokenLength)
            return 0;

        var lineNumber = 1;
        var tokenLength = 0;
        for (var index = 0; index < content.Length;)
        {
            var current = content[index];
            if (current <= '\u007F')
            {
                index++;
                if (current == '\n')
                {
                    lineNumber++;
                    tokenLength = 0;
                    continue;
                }

                if (IsLikelyUnicode61AsciiTokenChar(current))
                {
                    tokenLength++;
                    if (tokenLength > maxTokenLength)
                        return lineNumber;
                }
                else
                {
                    tokenLength = 0;
                }

                continue;
            }

            if (char.IsSurrogate(current))
            {
                if (!char.IsHighSurrogate(current)
                    || index + 1 >= content.Length
                    || !char.IsLowSurrogate(content[index + 1]))
                {
                    index++;
                    tokenLength = 0;
                    continue;
                }

                var surrogateRune = new Rune(current, content[index + 1]);
                index += 2;
                if (IsLikelyUnicode61TokenRune(surrogateRune))
                {
                    tokenLength++;
                    if (tokenLength > maxTokenLength)
                        return lineNumber;
                }
                else
                {
                    tokenLength = 0;
                }

                continue;
            }

            var rune = new Rune(current);
            index++;
            if (IsLikelyUnicode61TokenRune(rune))
            {
                tokenLength++;
                if (tokenLength > maxTokenLength)
                    return lineNumber;
            }
            else
            {
                tokenLength = 0;
            }
        }

        return 0;
    }

    private static bool IsLikelyUnicode61AsciiTokenChar(char value)
        => value == '_'
            || (value >= 'A' && value <= 'Z')
            || (value >= 'a' && value <= 'z')
            || (value >= '0' && value <= '9');

    private static bool IsLikelyUnicode61TokenRune(Rune rune)
        => rune.Value == '_'
            || Rune.IsLetter(rune)
            || Rune.IsDigit(rune)
            || Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark;
}
