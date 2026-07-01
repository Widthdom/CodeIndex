using System.Text;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private static void AddUtf16BomIssue(List<FileIssue> issues, string relativePath, bool utf16BigEndian)
    {
        issues.Add(new FileIssue
        {
            Path = relativePath,
            Kind = "utf16_bom",
            Line = 1,
            Message = utf16BigEndian
                ? "UTF-16 BE BOM detected (decoded as UTF-16)"
                : "UTF-16 LE BOM detected (decoded as UTF-16)",
            Origin = FileIssue.OriginByteOrderMark,
            Severity = FileIssue.SeverityWarning,
        });
    }

    private static void AddUtf16HeuristicIssue(List<FileIssue> issues, string relativePath, bool utf16BigEndian)
    {
        issues.Add(new FileIssue
        {
            Path = relativePath,
            Kind = "utf16_heuristic",
            Line = 1,
            Message = utf16BigEndian
                ? "BOM-less UTF-16 BE detected by NUL-byte heuristic (decoded as UTF-16)"
                : "BOM-less UTF-16 LE detected by NUL-byte heuristic (decoded as UTF-16)",
        });
    }

    private static void AddReplacementCharacterIssues(
        List<FileIssue> issues,
        string relativePath,
        byte[] rawBytes,
        string content,
        bool isUtf16,
        bool utf16BigEndian,
        bool hasUtf16Bom)
    {
        // Aggregate signal: when a large fraction of the decoded content is U+FFFD, the file
        // most likely uses a non-UTF8 encoding without a BOM (SHIFT_JIS / GBK / ISO-8859-1).
        // Emit one `non_utf8_likely` issue and suppress the per-line `replacement_char`
        // emission below so a mangled mojibake file does not produce hundreds of near-duplicate
        // issues that drown the actual diagnostic. The minimum count of 5 avoids tripping on
        // tiny stub files that happen to contain a single bad byte. Closes #1540.
        // 集約シグナル: デコード後の content に U+FFFD が大量にあるファイルは BOM 無し
        // 非 UTF-8 (SHIFT_JIS / GBK / ISO-8859-1) の可能性が高い。`non_utf8_likely` 1 件
        // を出し下の `replacement_char` 行単位出力は抑止する。1% 閾値だけだと大ファイル
        // で数百件の重複が出てしまい本来の診断を埋もれさせるためアグリゲートで代替。
        // 最低 5 件しきい値で、たまたま 1 byte 壊れた小さなスタブを誤検出しないように。
        // Closes #1540.
        const double NonUtf8LikelyRatioThreshold = 0.01;
        const int NonUtf8LikelyMinCount = 5;
        var fffdCount = CountReplacementChars(content);
        var replacementCharOrigin = fffdCount > 0
            ? DetermineReplacementCharOrigin(rawBytes, isUtf16, utf16BigEndian, hasUtf16Bom)
            : null;
        var nonUtf8Likely = replacementCharOrigin == FileIssue.OriginDecodeReplacement
            && fffdCount >= NonUtf8LikelyMinCount
            && content.Length > 0
            && (double)fffdCount / content.Length >= NonUtf8LikelyRatioThreshold;
        if (nonUtf8Likely)
        {
            var ratioPercent = 100.0 * fffdCount / content.Length;
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "non_utf8_likely",
                Line = 0,
                Message = $"Likely non-UTF8 encoding ({fffdCount} U+FFFD over {content.Length} chars, {ratioPercent:F1}%); source may be SHIFT_JIS, GBK, ISO-8859-1, or UTF-16 without BOM",
                Origin = FileIssue.OriginDecodeReplacement,
                Severity = FileIssue.SeverityWarning,
            });
        }

        // U+FFFD replacement characters baked into the file / ファイルに焼き付いたU+FFFD置換文字
        // Skip the per-line emission when `non_utf8_likely` already fired so a mojibake file
        // does not produce hundreds of near-duplicate `replacement_char` issues.
        // `non_utf8_likely` が出た場合は重複を抑え 1 件のアグリゲートに集約する。
        if (nonUtf8Likely)
            return;

        var lineNum = 1;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                lineNum++;
                continue;
            }

            if (content[i] != '\uFFFD')
                continue;

            var isSourceLiteral = replacementCharOrigin == FileIssue.OriginSourceLiteral;
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "replacement_char",
                Line = lineNum,
                Message = isSourceLiteral
                    ? $"U+FFFD source literal at line {lineNum}"
                    : $"U+FFFD decoder replacement character at line {lineNum}",
                Origin = replacementCharOrigin,
                Severity = isSourceLiteral ? FileIssue.SeverityInfo : FileIssue.SeverityWarning,
            });
            // Skip to next line to avoid reporting every char on the same line
            // 同じ行の連続報告を避けるため次の行までスキップ
            var nextNewline = content.IndexOf('\n', i);
            if (nextNewline < 0)
                break;
            lineNum++;
            i = nextNewline;
        }
    }

    private static void AddRawByteContentIssues(
        List<FileIssue> issues,
        string relativePath,
        RawByteContentInspection rawByteInspection)
    {
        // BOM marker / BOMマーカー
        if (rawByteInspection.HasUtf8Bom && !ShouldSuppressUtf8BomIssue(relativePath))
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "bom",
                Line = 1,
                Message = "UTF-8 BOM marker detected",
                Origin = FileIssue.OriginByteOrderMark,
                Severity = FileIssue.SeverityWarning,
            });
        }

        // NULL bytes (likely binary content) / NULLバイト（バイナリ混入の可能性）
        if (rawByteInspection.HasNullByte)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "null_byte",
                Line = 0,
                Message = "File contains NULL bytes (possible binary content)",
            });
        }

        AddLineEndingIssues(issues, relativePath, rawByteInspection);
    }

    private static bool ShouldSuppressUtf8BomIssue(string relativePath)
        => Path.GetExtension(Path.GetFileName(relativePath.AsSpan()))
            .Equals(".sln".AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static void AddLineEndingIssues(
        List<FileIssue> issues,
        string relativePath,
        RawByteContentInspection inspection)
    {
        var distinctEndingTypes = (inspection.HasCrlf ? 1 : 0)
            + (inspection.HasLfOnly ? 1 : 0)
            + (inspection.HasCrOnly ? 1 : 0);
        if (distinctEndingTypes >= 3)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "mixed_line_endings_three_way",
                Line = 0,
                Message = "Mixed line endings (CRLF, LF, and CR)",
            });
        }
        else if (distinctEndingTypes == 2)
        {
            string description;
            if (inspection.HasCrlf && inspection.HasLfOnly)
                description = "CRLF and LF";
            else if (inspection.HasCrlf && inspection.HasCrOnly)
                description = "CRLF and CR";
            else
                description = "LF and CR";
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "mixed_line_endings",
                Line = 0,
                Message = $"Mixed line endings ({description})",
            });
        }
        else if (inspection.HasCrOnly)
        {
            issues.Add(new FileIssue
            {
                Path = relativePath,
                Kind = "cr_only_line_endings",
                Line = 0,
                Message = "CR-only line endings (legacy Mac)",
            });
        }
    }

    /// <summary>
    /// Count U+FFFD replacement characters in decoded content.
    /// デコード済みcontent内のU+FFFD置換文字数を計上する。
    /// </summary>
    private static int CountReplacementChars(string content)
    {
        var firstReplacement = content.IndexOf('\uFFFD');
        if (firstReplacement < 0)
            return 0;

        var count = 1;
        for (int i = firstReplacement + 1; i < content.Length; i++)
        {
            if (content[i] == '\uFFFD') count++;
        }
        return count;
    }

    private static string DetermineReplacementCharOrigin(byte[] rawBytes, bool isUtf16, bool utf16BigEndian, bool hasUtf16Bom)
    {
        try
        {
            if (isUtf16)
            {
                _ = new UnicodeEncoding(utf16BigEndian, byteOrderMark: hasUtf16Bom, throwOnInvalidBytes: true)
                    .GetString(rawBytes);
            }
            else
            {
                _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(rawBytes);
            }

            return FileIssue.OriginSourceLiteral;
        }
        catch (DecoderFallbackException)
        {
            return FileIssue.OriginDecodeReplacement;
        }
    }
}
