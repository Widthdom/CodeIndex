namespace CodeIndex.Models;

/// <summary>
/// Represents an encoding or content issue found in a file.
/// ファイルで見つかったエンコーディングまたは内容の問題を表す。
/// </summary>
public class FileIssue
{
    public const string OriginSourceLiteral = "source_literal";
    public const string OriginDecodeReplacement = "decode_replacement";
    public const string OriginByteOrderMark = "byte_order_mark";
    public const string SeverityInfo = "info";
    public const string SeverityWarning = "warning";
    public const string CategoryExpectedFixtureLiteral = "expected_fixture_literal";
    public const string CategoryIntentionalSourceLiteral = "intentional_source_literal";
    public const string CategoryDecodingRisk = "decoding_risk";
    public const string CategoryByteOrderMark = "byte_order_mark";
    public const string CategoryLineEndings = "line_endings";
    public const string CategoryRawBytes = "raw_bytes";
    public const string CategoryContentLimit = "content_limit";
    public const string CategoryContentStructure = "content_structure";
    public const string CategoryOther = "other";

    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Origin { get; set; }
    public string? Severity { get; set; }
    public string? Category { get; set; }
    public bool? Actionable { get; set; }
}
