using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    /// <summary>
    /// Validate file content for encoding issues.
    /// ファイル内容のエンコーディング問題を検証する。
    /// </summary>
    public static List<FileIssue> ValidateContent(string relativePath, byte[] rawBytes, string content, string? language = null)
        => ValidateContent(relativePath, rawBytes, content, language, FileContentInspection.Inspect(rawBytes));

    internal static List<FileIssue> ValidateContent(
        string relativePath,
        byte[] rawBytes,
        string content,
        string? language,
        FileContentInspection inspection,
        bool? hasOversizeLine = null,
        int? conflictMarkerLine = null)
    {
        var issues = new List<FileIssue>();

        if (inspection.IsGitLfsPointer)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "lfs_pointer_skipped",
                Line = 1,
                Message = "Git LFS pointer file skipped; fetch LFS objects to index real file content",
            });
        }

        // UTF-16 BOM-detected files are decoded as UTF-16 in BuildRecordWithRawBytes, so the
        // raw-byte heuristics for `bom` / `null_byte` / `mixed_line_endings` would all misfire
        // (every UTF-16 LE character ASCII point looks like a NUL byte; CRLF appears as 0D 00
        // 0A 00). Emit a single `utf16_bom` issue instead so `validate` clearly explains the
        // file was decoded via UTF-16. The content-side U+FFFD check still runs so genuine
        // invalid surrogate pairs are reported. Closes #1540.
        // UTF-16 BOM 検出ファイルは BuildRecordWithRawBytes で UTF-16 デコード済みのため、
        // 生バイト系の `bom` / `null_byte` / `mixed_line_endings` 判定はすべて誤検出する
        // (UTF-16 LE では ASCII 部の片バイトが NUL、CRLF は 0D 00 0A 00)。代わりに
        // `utf16_bom` 1 件を出して `validate` が「UTF-16 として解釈した」ことを示し、
        // 不正サロゲートペアに備え content 側 U+FFFD 走査は継続する。Closes #1540.
        var isUtf16 = inspection.IsUtf16;
        var utf16BigEndian = inspection.Utf16BigEndian;
        var hasUtf16Bom = inspection.HasUtf16Bom;

        if (isUtf16)
        {
            if (hasUtf16Bom)
                AddUtf16BomIssue(issues, relativePath, utf16BigEndian);
            else
                AddUtf16HeuristicIssue(issues, relativePath, utf16BigEndian);
        }

        var effectiveConflictMarkerLine = conflictMarkerLine ?? GetConflictMarkerLine(content);
        if (effectiveConflictMarkerLine > 0)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "conflict_markers",
                Line = effectiveConflictMarkerLine,
                Message = "Git conflict markers detected; resolve the conflict before indexing symbols or references",
            });
        }

        AddReplacementCharacterIssues(issues, relativePath, rawBytes, content, isUtf16, utf16BigEndian, hasUtf16Bom);

        // Raw-byte heuristics: skip for UTF-16-decoded files because every UTF-16 LE ASCII
        // codepoint looks like a NUL byte and CRLF appears as 0D 00 0A 00, so `bom` /
        // `null_byte` / `mixed_line_endings` / `cr_only_line_endings` would all misfire.
        // UTF-16 デコード経路では生バイト列が NUL バイトと 0D 00 0A 00 で埋まり、`bom` /
        // `null_byte` / `mixed_line_endings` / `cr_only_line_endings` がすべて誤検出する
        // ためスキップする。
        if (!isUtf16)
            AddRawByteContentIssues(issues, relativePath, inspection.RawByteContent);

        AddOversizeContentIssues(issues, relativePath, content, hasOversizeLine);
        var effectiveLanguage = language ?? TryDetectLanguage(relativePath, content).Language;
        if (effectiveLanguage is "xml" or "msbuild")
            AddXmlStructureIssues(issues, relativePath, content);
        if (effectiveLanguage == "dockerfile")
        {
            AddDockerfileJsonFormIssues(issues, relativePath, content);
        }

        return issues;
    }

    private static void AddXmlStructureIssues(List<FileIssue> issues, string relativePath, string content)
    {
        if (!SymbolExtractor.TryGetXmlStructureIssue(content, out var issue))
            return;

        issues.Add(new FileIssue
        {
            Path = relativePath,
            Kind = issue.Kind,
            Line = issue.Line,
            Message = issue.Message,
            Severity = FileIssue.SeverityWarning,
        });
    }
}
